const { normalizePath } = require('./terminal-matcher');

const bridgeTokenMaxAgeMs = 24 * 60 * 60 * 1000;

function selectRecoverableToken(bindings, terminal, now = Date.now()) {
  const workspaceFolders = normalizeFolders(terminal.workspaceFolders);
  const matches = (bindings || []).filter((binding) => {
    const token = normalizeToken(binding.terminalToken);
    if (!token || binding.processId !== terminal.processId) {
      return false;
    }

    const bindingFolders = normalizeFolders(binding.workspaceFolders);
    if (JSON.stringify(bindingFolders) !== JSON.stringify(workspaceFolders)) {
      return false;
    }

    const kind = binding.bindingKind || 'managedEnvironment';
    if (kind === 'managedEnvironment') {
      const tokenPrefix = token.slice(0, 8);
      return Boolean(tokenPrefix && terminal.name?.includes(`[${tokenPrefix}]`));
    }

    if (kind !== 'bridgeAssigned'
      || binding.terminalName !== terminal.name
      || !isRecent(binding.updatedAtUtc, now)) {
      return false;
    }

    return true;
  });

  const tokens = [...new Set(matches.map((binding) => binding.terminalToken))];
  return tokens.length === 1 ? tokens[0] : '';
}

function normalizeFolders(values) {
  return (values || []).map(normalizePath).filter(Boolean).sort();
}

function normalizeToken(value) {
  return typeof value === 'string' ? value.trim().toLowerCase() : '';
}

function isRecent(value, now) {
  const updatedAt = Date.parse(value || '');
  return Number.isFinite(updatedAt)
    && Math.abs(now - updatedAt) <= bridgeTokenMaxAgeMs;
}

module.exports = {
  bridgeTokenMaxAgeMs,
  selectRecoverableToken
};
