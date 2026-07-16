const fs = require('fs');
const os = require('os');
const path = require('path');
const vscode = require('vscode');

const requestDirectory = path.join(os.homedir(), '.cc-monitor');
const requestFile = path.join(requestDirectory, 'focus-terminal.json');
const resultFile = path.join(requestDirectory, 'focus-terminal-result.json');
const maxRequestAgeMs = 15000;

/** @param {vscode.ExtensionContext} context */
function activate(context) {
  const output = vscode.window.createOutputChannel('CC Monitor');
  let lastRequestId = '';
  let debounceTimer;

  const processRequest = async (explicitRequest) => {
    let request = explicitRequest;
    try {
      if (!request) {
        request = JSON.parse(await fs.promises.readFile(requestFile, 'utf8'));
      }

      if (!request || request.requestId === lastRequestId) {
        return;
      }

      const requestedAt = Date.parse(request.requestedAtUtc || '');
      if (!Number.isFinite(requestedAt) || Math.abs(Date.now() - requestedAt) > maxRequestAgeMs) {
        return;
      }

      const match = await findTerminal(request);
      if (!match) {
        output.appendLine(`No terminal matched request ${request.requestId || '(unknown)'}.`);
        return;
      }

      lastRequestId = request.requestId || String(requestedAt);
      match.terminal.show(false);
      await writeResult(request, match);
      output.appendLine(`Focused terminal "${match.terminal.name}" by ${match.matchKind} for request ${lastRequestId}.`);
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        return;
      }
      output.appendLine(`Focus request failed: ${error instanceof Error ? error.message : String(error)}`);
    }
  };

  const scheduleRequest = () => {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => processRequest(), 80);
  };

  fs.mkdirSync(requestDirectory, { recursive: true });
  const watcher = fs.watch(requestDirectory, (_eventType, fileName) => {
    if (String(fileName || '').toLowerCase() === 'focus-terminal.json') {
      scheduleRequest();
    }
  });

  context.subscriptions.push(
    output,
    { dispose: () => watcher.close() },
    { dispose: () => clearTimeout(debounceTimer) },
    vscode.commands.registerCommand('ccMonitor.focusTerminal', processRequest)
  );

  processRequest();
}

async function findTerminal(request) {
  const terminals = vscode.window.terminals;
  const requestedPid = Number(request.terminalProcessId);

  if (Number.isInteger(requestedPid) && requestedPid > 0) {
    for (const terminal of terminals) {
      if (await terminal.processId === requestedPid) {
        return { terminal, matchKind: 'processId' };
      }
    }
  }

  const requestedDirectory = normalizePath(request.workingDirectory);
  if (requestedDirectory) {
    const cwdMatch = terminals.find((terminal) => {
      const terminalDirectory = normalizePath(terminal.shellIntegration?.cwd?.fsPath);
      return terminalDirectory && pathsOverlap(terminalDirectory, requestedDirectory);
    });
    if (cwdMatch) {
      return { terminal: cwdMatch, matchKind: 'workingDirectory' };
    }

    const workspaceMatches = (vscode.workspace.workspaceFolders || []).some((folder) =>
      pathsOverlap(normalizePath(folder.uri.fsPath), requestedDirectory)
    );
    if (workspaceMatches && terminals.length === 1) {
      return { terminal: terminals[0], matchKind: 'singleWorkspaceTerminal' };
    }
  }

  return undefined;
}

async function writeResult(request, match) {
  const processId = await match.terminal.processId;
  const workspaceName = vscode.workspace.name || '';
  const result = {
    requestId: request.requestId,
    completedAtUtc: new Date().toISOString(),
    terminalProcessId: processId,
    terminalName: match.terminal.name,
    workspaceName,
    matchKind: match.matchKind
  };
  const tempFile = `${resultFile}.${process.pid}.${Date.now()}.tmp`;
  await fs.promises.writeFile(tempFile, JSON.stringify(result), 'utf8');
  await fs.promises.rename(tempFile, resultFile).catch(async (error) => {
    if (error && (error.code === 'EEXIST' || error.code === 'EPERM')) {
      await fs.promises.rm(resultFile, { force: true });
      await fs.promises.rename(tempFile, resultFile);
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

function pathsOverlap(left, right) {
  return left === right || left.startsWith(`${right}${path.sep}`) || right.startsWith(`${left}${path.sep}`);
}

function deactivate() {}

module.exports = { activate, deactivate };
