const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const vscode = require('vscode');
const { normalizePath, selectLegacyTerminal } = require('./terminal-matcher');

const rootDirectory = path.join(os.homedir(), '.cc-monitor');
const requestFile = path.join(rootDirectory, 'focus-terminal.json');
const resultFile = path.join(rootDirectory, 'focus-terminal-result.json');
const bindingRequestFile = path.join(rootDirectory, 'bind-terminal-session.json');
const registryDirectory = path.join(rootDirectory, 'terminal-bridges');
const tokenBindingsDirectory = path.join(rootDirectory, 'terminal-token-bindings');
const terminalBindingsDirectory = path.join(rootDirectory, 'terminal-bindings');
const sessionsDirectory = path.join(rootDirectory, 'sessions');
const maxRequestAgeMs = 15000;
const heartbeatIntervalMs = 2000;
const terminalTokenEnvironmentVariable = 'CCMONITOR_TERMINAL_TOKEN';

/** @param {vscode.ExtensionContext} context */
function activate(context) {
  const output = vscode.window.createOutputChannel('CC Monitor');
  const bridgeId = crypto.randomUUID().replace(/-/g, '');
  const registrationFile = path.join(registryDirectory, `${bridgeId}.json`);
  const managedTokens = new WeakMap();
  let lastRequestId = '';
  let debounceTimer;
  let registrationWrite = Promise.resolve();

  const refreshRegistration = () => {
    registrationWrite = registrationWrite
      .catch(() => undefined)
      .then(async () => {
        const terminals = await Promise.all(
          vscode.window.terminals.map((terminal) =>
            snapshotTerminal(terminal, managedTokens, output))
        );
        const registration = {
          protocolVersion: 3,
          bridgeId,
          processId: process.pid,
          updatedAtUtc: new Date().toISOString(),
          workspaceName: vscode.workspace.name || '',
          workspaceFolders: (vscode.workspace.workspaceFolders || []).map(
            (folder) => folder.uri.fsPath
          ),
          terminals
        };
        await writeAtomic(registrationFile, registration);
      })
      .catch((error) => {
        output.appendLine(`Bridge registration failed: ${formatError(error)}`);
      });
  };

  const createManagedClaudeTerminal = async (cwd, source) => {
    const token = crypto.randomUUID().replace(/-/g, '');
    const options = {
      name: `CC Claude [${token.slice(0, 8)}]`,
      env: {
        [terminalTokenEnvironmentVariable]: token
      }
    };
    if (cwd) {
      options.cwd = cwd;
    }

    const terminal = vscode.window.createTerminal(options);
    managedTokens.set(terminal, token);
    terminal.show(false);
    terminal.processId
      .then((processId) => rememberTokenBinding(terminal, token, processId))
      .catch((error) => output.appendLine(`Token binding failed: ${formatError(error)}`));
    await refreshRegistration();
    terminal.sendText('claude', true);
    output.appendLine(
      `Created managed Claude terminal token=${token.slice(0, 8)} source=${source} cwd=${cwd || 'default'}.`
    );
    return terminal;
  };

  const createManagedFromWorkspace = async () => {
    const cwd = vscode.workspace.workspaceFolders?.[0]?.uri;
    await createManagedClaudeTerminal(cwd, 'workspace');
  };

  const migrateActiveTerminal = async () => {
    const active = vscode.window.activeTerminal;
    if (!active) {
      vscode.window.showErrorMessage('CC Monitor: No active terminal is available to migrate.');
      return;
    }

    const choice = await vscode.window.showWarningMessage(
      'CC Monitor will create a new tokenized terminal at the same working directory and start Claude. '
        + 'The existing terminal will remain open; stop its Claude session when convenient.',
      { modal: true },
      'Create managed terminal'
    );
    if (choice !== 'Create managed terminal') {
      return;
    }

    const cwd = active.shellIntegration?.cwd || vscode.workspace.workspaceFolders?.[0]?.uri;
    await createManagedClaudeTerminal(cwd, 'active-terminal-migration');
  };

  const bindActiveTerminalToSession = async () => {
    const active = vscode.window.activeTerminal;
    if (!active) {
      vscode.window.showErrorMessage(
        'CC Monitor: Select the terminal to bind before running this command.'
      );
      return;
    }

    const session = await selectSessionForBinding();
    if (!session) {
      return;
    }

    const snapshot = await snapshotTerminal(active, managedTokens, output);
    if (!snapshot.processId && !snapshot.terminalToken) {
      vscode.window.showErrorMessage(
        'CC Monitor: The active terminal has no process ID or terminal token yet.'
      );
      return;
    }

    const binding = {
      sessionId: session.sessionId,
      terminalToken: snapshot.terminalToken || '',
      terminalProcessId: snapshot.processId,
      terminalName: snapshot.name,
      workingDirectory: snapshot.workingDirectory,
      updatedAtUtc: new Date().toISOString()
    };
    await writeAtomic(
      path.join(terminalBindingsDirectory, `${sanitizeFileName(session.sessionId)}.json`),
      binding
    );
    await fs.promises.rm(bindingRequestFile, { force: true });
    refreshRegistration();
    output.appendLine(
      `Bound session ${session.sessionId} to terminal "${snapshot.name}" `
      + `pid=${snapshot.processId || 'n/a'} token=${shortToken(snapshot.terminalToken)}.`
    );
    vscode.window.showInformationMessage(
      `CC Monitor: Bound ${session.projectName || session.sessionId.slice(0, 8)} `
      + `to terminal "${snapshot.name}".`
    );
  };

  const processRequest = async (explicitRequest) => {
    let request = explicitRequest;
    try {
      if (!request) {
        request = JSON.parse(await fs.promises.readFile(requestFile, 'utf8'));
      }

      if (!request
        || request.protocolVersion < 2
        || request.protocolVersion > 3
        || request.targetBridgeId !== bridgeId) {
        return;
      }

      if (!request.requestId || request.requestId === lastRequestId) {
        return;
      }

      const requestedAt = Date.parse(request.requestedAtUtc || '');
      if (!Number.isFinite(requestedAt) || Math.abs(Date.now() - requestedAt) > maxRequestAgeMs) {
        output.appendLine(`Ignored stale focus request ${request.requestId}.`);
        return;
      }

      lastRequestId = request.requestId;
      const match = await findTerminal(request, managedTokens, output);
      if (!match.terminal) {
        await writeResult(request, {
          status: 'noMatch',
          matchKind: match.matchKind,
          reason: match.reason
        });
        output.appendLine(`No terminal matched request ${request.requestId}: ${match.reason}`);
        return;
      }

      match.terminal.show(false);
      const windowFocus = await focusBridgeWindow();
      await writeResult(request, {
        status: 'matched',
        terminal: match.terminal,
        terminalToken: match.terminalToken,
        matchKind: match.matchKind,
        reason: match.reason,
        windowFocused: windowFocus.focused,
        windowFocusReason: windowFocus.reason
      });
      output.appendLine(
        `Focused terminal "${match.terminal.name}" by ${match.matchKind} for request ${request.requestId}; `
        + `windowFocused=${windowFocus.focused} (${windowFocus.reason}).`
      );
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        return;
      }
      output.appendLine(`Focus request failed: ${formatError(error)}`);
      if (request?.requestId && request?.targetBridgeId === bridgeId) {
        await writeResult(request, {
          status: 'noMatch',
          matchKind: 'bridgeError',
          reason: formatError(error)
        }).catch(() => undefined);
      }
    } finally {
      refreshRegistration();
    }
  };

  const scheduleRequest = () => {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => processRequest(), 80);
  };

  fs.mkdirSync(registryDirectory, { recursive: true });
  fs.mkdirSync(tokenBindingsDirectory, { recursive: true });
  const watcher = fs.watch(rootDirectory, (_eventType, fileName) => {
    if (String(fileName || '').toLowerCase() === 'focus-terminal.json') {
      scheduleRequest();
    }
  });
  const heartbeatTimer = setInterval(refreshRegistration, heartbeatIntervalMs);

  context.subscriptions.push(
    output,
    { dispose: () => watcher.close() },
    { dispose: () => clearInterval(heartbeatTimer) },
    { dispose: () => clearTimeout(debounceTimer) },
    {
      dispose: () => {
        fs.rmSync(registrationFile, { force: true });
      }
    },
    vscode.window.onDidOpenTerminal(refreshRegistration),
    vscode.window.onDidCloseTerminal((terminal) => {
      removeTokenBinding(terminal, managedTokens)
        .catch((error) => output.appendLine(`Token binding cleanup failed: ${formatError(error)}`))
        .finally(refreshRegistration);
    }),
    vscode.window.onDidChangeActiveTerminal(refreshRegistration),
    vscode.workspace.onDidChangeWorkspaceFolders(refreshRegistration),
    vscode.commands.registerCommand('ccMonitor.focusTerminal', processRequest),
    vscode.commands.registerCommand(
      'ccMonitor.createManagedClaudeTerminal',
      createManagedFromWorkspace
    ),
    vscode.commands.registerCommand(
      'ccMonitor.migrateActiveTerminal',
      migrateActiveTerminal
    ),
    vscode.commands.registerCommand(
      'ccMonitor.bindActiveTerminalToSession',
      bindActiveTerminalToSession
    )
  );

  output.appendLine(`Terminal Bridge v3 started bridge=${bridgeId} pid=${process.pid}.`);
  refreshRegistration();
  processRequest();

  async function writeResult(request, result) {
    const terminal = result.terminal;
    const processId = terminal ? await terminal.processId : undefined;
    const payload = {
      requestId: request.requestId,
      completedAtUtc: new Date().toISOString(),
      status: result.status,
      terminalProcessId: processId,
      terminalName: terminal?.name || '',
      terminalToken: result.terminalToken || '',
      workspaceName: vscode.workspace.name || '',
      matchKind: result.matchKind || '',
      reason: result.reason || '',
      windowFocused: result.windowFocused,
      windowFocusReason: result.windowFocusReason || '',
      bridgeId
    };
    await writeAtomic(resultFile, payload);
  }
}

async function focusBridgeWindow() {
  try {
    const commands = await vscode.commands.getCommands(true);
    if (!commands.includes('workbench.action.focusWindow')) {
      return {
        focused: false,
        reason: 'VS Code does not expose workbench.action.focusWindow.'
      };
    }

    await vscode.commands.executeCommand('workbench.action.focusWindow');
    return {
      focused: true,
      reason: 'The target extension host asked its own VS Code window to take focus.'
    };
  } catch (error) {
    return {
      focused: false,
      reason: `VS Code window focus command failed: ${formatError(error)}`
    };
  }
}

async function snapshotTerminal(terminal, managedTokens, output) {
  const processId = await terminal.processId;
  const terminalToken = await resolveTerminalToken(terminal, processId, managedTokens);
  if (terminalToken) {
    managedTokens.set(terminal, terminalToken);
    rememberTokenBinding(terminal, terminalToken, processId)
      .catch((error) => output.appendLine(`Token binding refresh failed: ${formatError(error)}`));
  }
  return {
    terminalId: terminalToken
      ? `token:${terminalToken}`
      : `${processId || 'no-pid'}:${terminal.name}`,
    terminalToken,
    name: terminal.name,
    processId,
    workingDirectory: terminal.shellIntegration?.cwd?.fsPath || ''
  };
}

async function findTerminal(request, managedTokens, output) {
  const snapshots = await Promise.all(
    vscode.window.terminals.map(async (terminal) => {
      const snapshot = await snapshotTerminal(terminal, managedTokens, output);
      return {
        terminal,
        terminalToken: snapshot.terminalToken,
        processId: snapshot.processId,
        workingDirectory: normalizePath(snapshot.workingDirectory)
      };
    })
  );
  const requestedToken = normalizeToken(request.terminalToken);
  const requestedProcessId = Number(request.terminalProcessId);
  if (Number.isInteger(requestedProcessId) && requestedProcessId > 0) {
    const manualMatches = snapshots.filter(
      (snapshot) =>
        snapshot.processId === requestedProcessId
        || (requestedToken
          && normalizeToken(snapshot.terminalToken) === requestedToken)
    );
    if (manualMatches.length === 1) {
      return {
        terminal: manualMatches[0].terminal,
        terminalToken: manualMatches[0].terminalToken,
        matchKind: 'manualBinding',
        reason: 'A single terminal matched the explicit session-terminal binding.'
      };
    }
    return {
      matchKind: manualMatches.length === 0
        ? 'manualTerminalNotRegistered'
        : 'ambiguousManualBinding',
      reason: manualMatches.length === 0
        ? 'The manually bound terminal is no longer registered. Bind the session again.'
        : `${manualMatches.length} terminals matched the explicit session-terminal binding.`
    };
  }

  if (requestedToken) {
    const tokenMatches = snapshots.filter(
      (snapshot) => normalizeToken(snapshot.terminalToken) === requestedToken
    );
    if (tokenMatches.length === 1) {
      return {
        terminal: tokenMatches[0].terminal,
        terminalToken: tokenMatches[0].terminalToken,
        matchKind: 'terminalToken',
        reason: 'A single terminal had the exact session terminal token.'
      };
    }
    return {
      matchKind: tokenMatches.length === 0
        ? 'terminalTokenNotRegistered'
        : 'ambiguousTerminalToken',
      reason: tokenMatches.length === 0
        ? 'No terminal in the selected window registered the session terminal token.'
        : `${tokenMatches.length} terminals registered the same session terminal token.`
    };
  }

  const legacyMatch = selectLegacyTerminal(
    snapshots,
    request.workingDirectory,
    (vscode.workspace.workspaceFolders || []).map((folder) => folder.uri.fsPath)
  );
  if (legacyMatch.matchKind !== 'noMatch') {
    return legacyMatch;
  }

  const requestedDirectory = normalizePath(request.workingDirectory);
  const terminalSummary = snapshots
    .map((snapshot) =>
      `${snapshot.terminal.name}[pid=${snapshot.processId || 'n/a'},`
      + `token=${shortToken(snapshot.terminalToken)},`
      + `cwd=${snapshot.workingDirectory || 'n/a'}]`)
    .join(', ');
  return {
    matchKind: 'noMatch',
    reason: `cwd=${requestedDirectory || 'n/a'} terminals=${terminalSummary || 'none'}`
  };
}

async function resolveTerminalToken(terminal, processId, managedTokens) {
  const remembered = managedTokens.get(terminal);
  if (remembered) {
    return remembered;
  }

  const environment = terminal.creationOptions?.env;
  if (!environment || typeof environment !== 'object') {
    return recoverTokenBinding(terminal, processId);
  }

  const value = environment[terminalTokenEnvironmentVariable]
    || environment[terminalTokenEnvironmentVariable.toLowerCase()];
  if (typeof value === 'string' && value) {
    return value;
  }

  return recoverTokenBinding(terminal, processId);
}

async function rememberTokenBinding(terminal, terminalToken, processId) {
  if (!terminalToken || !processId) {
    return;
  }

  const payload = {
    terminalToken,
    processId,
    terminalName: terminal.name,
    workspaceName: vscode.workspace.name || '',
    workspaceFolders: (vscode.workspace.workspaceFolders || []).map(
      (folder) => normalizePath(folder.uri.fsPath)
    ),
    updatedAtUtc: new Date().toISOString()
  };
  await writeAtomic(
    path.join(tokenBindingsDirectory, `${terminalToken}.json`),
    payload
  );
}

async function recoverTokenBinding(terminal, processId) {
  if (!processId || !terminal.name) {
    return '';
  }

  let fileNames;
  try {
    fileNames = await fs.promises.readdir(tokenBindingsDirectory);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return '';
    }
    throw error;
  }

  const workspaceFolders = (vscode.workspace.workspaceFolders || [])
    .map((folder) => normalizePath(folder.uri.fsPath))
    .sort();
  for (const fileName of fileNames.filter((name) => name.endsWith('.json'))) {
    try {
      const binding = JSON.parse(
        await fs.promises.readFile(path.join(tokenBindingsDirectory, fileName), 'utf8')
      );
      const token = normalizeToken(binding.terminalToken);
      const tokenPrefix = token.slice(0, 8);
      const bindingFolders = (binding.workspaceFolders || []).map(normalizePath).sort();
      if (binding.processId === processId
        && tokenPrefix
        && terminal.name.includes(`[${tokenPrefix}]`)
        && JSON.stringify(bindingFolders) === JSON.stringify(workspaceFolders)) {
        return binding.terminalToken;
      }
    } catch {
      // Ignore stale or partially written binding files.
    }
  }

  return '';
}

async function removeTokenBinding(terminal, managedTokens) {
  let processId;
  try {
    processId = await terminal.processId;
  } catch {
    processId = undefined;
  }
  const terminalToken = await resolveTerminalToken(terminal, processId, managedTokens);
  if (!terminalToken) {
    return;
  }

  managedTokens.delete(terminal);
  await fs.promises.rm(
    path.join(tokenBindingsDirectory, `${terminalToken}.json`),
    { force: true }
  );
}

async function selectSessionForBinding() {
  const pending = await tryReadJson(bindingRequestFile);
  if (pending?.sessionId) {
    const requestedAt = Date.parse(pending.requestedAtUtc || '');
    if (Number.isFinite(requestedAt) && Date.now() - requestedAt <= 10 * 60 * 1000) {
      return {
        sessionId: pending.sessionId,
        projectName: pending.projectName || '',
        workingDirectory: pending.workingDirectory || ''
      };
    }
  }

  let fileNames;
  try {
    fileNames = await fs.promises.readdir(sessionsDirectory);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      vscode.window.showErrorMessage('CC Monitor: No sessions were found to bind.');
      return undefined;
    }
    throw error;
  }

  const sessions = [];
  for (const fileName of fileNames.filter((name) => name.endsWith('.json'))) {
    const state = await tryReadJson(path.join(sessionsDirectory, fileName));
    const sessionId = state?.SessionId || state?.sessionId;
    if (!sessionId) {
      continue;
    }
    sessions.push({
      sessionId,
      projectName: state.ProjectName || state.projectName || '',
      workingDirectory: state.WorkingDirectory || state.workingDirectory || '',
      updatedAt: Date.parse(state.UpdatedAt || state.updatedAt || '') || 0
    });
  }

  sessions.sort((left, right) => right.updatedAt - left.updatedAt);
  const picked = await vscode.window.showQuickPick(
    sessions.map((session) => ({
      label: session.projectName || 'Unknown project',
      description: session.sessionId.slice(0, 8),
      detail: session.workingDirectory || 'No working directory',
      session
    })),
    {
      title: 'Bind active terminal to a CC Monitor session',
      placeHolder: 'Choose the session that belongs to the active terminal'
    }
  );
  return picked?.session;
}

async function tryReadJson(filePath) {
  try {
    return JSON.parse(await fs.promises.readFile(filePath, 'utf8'));
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return undefined;
    }
    return undefined;
  }
}

async function writeAtomic(filePath, value) {
  await fs.promises.mkdir(path.dirname(filePath), { recursive: true });
  const tempFile =
    `${filePath}.${process.pid}.${Date.now()}.${crypto.randomBytes(4).toString('hex')}.tmp`;
  await fs.promises.writeFile(tempFile, JSON.stringify(value), 'utf8');
  await fs.promises.rename(tempFile, filePath).catch(async (error) => {
    if (error && (error.code === 'EEXIST' || error.code === 'EPERM')) {
      await fs.promises.rm(filePath, { force: true });
      await fs.promises.rename(tempFile, filePath);
      return;
    }
    throw error;
  });
}

function normalizeToken(value) {
  return typeof value === 'string' ? value.trim().toLowerCase() : '';
}

function shortToken(value) {
  const normalized = normalizeToken(value);
  return normalized ? normalized.slice(0, 8) : 'none';
}

function sanitizeFileName(value) {
  return String(value || '').replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_');
}

function formatError(error) {
  return error instanceof Error ? error.message : String(error);
}

function deactivate() {}

module.exports = { activate, deactivate };
