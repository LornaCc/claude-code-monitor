const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const vscode = require('vscode');

const rootDirectory = path.join(os.homedir(), '.cc-monitor');
const requestFile = path.join(rootDirectory, 'focus-terminal.json');
const resultFile = path.join(rootDirectory, 'focus-terminal-result.json');
const registryDirectory = path.join(rootDirectory, 'terminal-bridges');
const tokenBindingsDirectory = path.join(rootDirectory, 'terminal-token-bindings');
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
      await writeResult(request, {
        status: 'matched',
        terminal: match.terminal,
        terminalToken: match.terminalToken,
        matchKind: match.matchKind,
        reason: match.reason
      });
      output.appendLine(
        `Focused terminal "${match.terminal.name}" by ${match.matchKind} for request ${request.requestId}.`
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
      bridgeId
    };
    await writeAtomic(resultFile, payload);
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

  const requestedDirectory = normalizePath(request.workingDirectory);
  if (requestedDirectory) {
    const exactMatches = snapshots.filter(
      (snapshot) => snapshot.workingDirectory === requestedDirectory
    );
    if (exactMatches.length === 1) {
      return {
        terminal: exactMatches[0].terminal,
        terminalToken: exactMatches[0].terminalToken,
        matchKind: 'exactWorkingDirectory',
        reason: 'A single legacy terminal had the exact requested working directory.'
      };
    }
    if (exactMatches.length > 1) {
      return {
        matchKind: 'ambiguousExactWorkingDirectory',
        reason: `${exactMatches.length} legacy terminals had the exact requested working directory. `
          + 'Use CC Monitor: Migrate Active Terminal to create a tokenized terminal.'
      };
    }

    if (isSpecificProjectPath(requestedDirectory)) {
      const descendantMatches = snapshots.filter(
        (snapshot) =>
          snapshot.workingDirectory
          && isDescendant(snapshot.workingDirectory, requestedDirectory)
      );
      if (descendantMatches.length === 1) {
        return {
          terminal: descendantMatches[0].terminal,
          terminalToken: descendantMatches[0].terminalToken,
          matchKind: 'workingDirectoryDescendant',
          reason: 'A single legacy terminal was inside the requested project directory.'
        };
      }
      if (descendantMatches.length > 1) {
        return {
          matchKind: 'ambiguousWorkingDirectory',
          reason: `${descendantMatches.length} legacy terminals were inside the requested project directory. `
            + 'Use CC Monitor: Migrate Active Terminal to create a tokenized terminal.'
        };
      }
    }
  }

  const workspaceMatches = (vscode.workspace.workspaceFolders || []).some((folder) => {
    const workspaceDirectory = normalizePath(folder.uri.fsPath);
    return requestedDirectory === workspaceDirectory
      || isDescendant(requestedDirectory, workspaceDirectory);
  });
  if (workspaceMatches && snapshots.length === 1) {
    return {
      terminal: snapshots[0].terminal,
      terminalToken: snapshots[0].terminalToken,
      matchKind: 'singleWorkspaceTerminal',
      reason: 'The selected workspace had exactly one terminal.'
    };
  }

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

function normalizePath(value) {
  if (!value || typeof value !== 'string') {
    return '';
  }
  return path.resolve(value).replace(/[\\/]+$/, '').toLowerCase();
}

function normalizeToken(value) {
  return typeof value === 'string' ? value.trim().toLowerCase() : '';
}

function shortToken(value) {
  const normalized = normalizeToken(value);
  return normalized ? normalized.slice(0, 8) : 'none';
}

function isDescendant(child, parent) {
  return Boolean(child && parent && child !== parent && child.startsWith(`${parent}${path.sep}`));
}

function isSpecificProjectPath(value) {
  const parsed = path.parse(value);
  const relative = value.slice(parsed.root.length);
  return relative.split(/[\\/]+/).filter(Boolean).length >= 3;
}

function formatError(error) {
  return error instanceof Error ? error.message : String(error);
}

function deactivate() {}

module.exports = { activate, deactivate };
