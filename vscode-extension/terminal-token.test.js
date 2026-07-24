const assert = require('node:assert/strict');
const test = require('node:test');

const {
  bridgeTokenMaxAgeMs,
  selectRecoverableToken
} = require('./terminal-token');

const now = Date.parse('2026-07-24T06:00:00Z');

function terminal(overrides = {}) {
  return {
    processId: 26100,
    name: 'bash',
    workingDirectory: 'C:\\work\\demo',
    workspaceFolders: ['C:\\work\\demo'],
    ...overrides
  };
}

function binding(overrides = {}) {
  return {
    terminalToken: '0123456789abcdef0123456789abcdef',
    processId: 26100,
    terminalName: 'bash',
    workingDirectory: 'C:\\work\\demo',
    workspaceFolders: ['C:\\work\\demo'],
    bindingKind: 'bridgeAssigned',
    updatedAtUtc: new Date(now - 1000).toISOString(),
    ...overrides
  };
}

test('recovers a bridge-assigned token for the same live terminal', () => {
  assert.equal(
    selectRecoverableToken([binding()], terminal(), now),
    '0123456789abcdef0123456789abcdef'
  );
});

test('keeps a terminal token when the shell changes working directory', () => {
  assert.equal(
    selectRecoverableToken(
      [binding()],
      terminal({ workingDirectory: 'C:\\work\\demo\\src' }),
      now
    ),
    '0123456789abcdef0123456789abcdef'
  );
});

test('does not recover a stale bridge-assigned token after PID reuse', () => {
  const stale = binding({
    updatedAtUtc: new Date(now - bridgeTokenMaxAgeMs - 1).toISOString()
  });
  assert.equal(selectRecoverableToken([stale], terminal(), now), '');
});

test('does not guess when duplicate persisted bindings match one terminal', () => {
  const duplicate = binding({
    terminalToken: 'fedcba9876543210fedcba9876543210'
  });
  assert.equal(selectRecoverableToken([binding(), duplicate], terminal(), now), '');
});

test('recovers a managed environment token by its terminal-name prefix', () => {
  const managed = binding({
    bindingKind: 'managedEnvironment',
    updatedAtUtc: 'not-required'
  });
  const managedTerminal = terminal({ name: 'CC Claude [01234567]' });
  assert.equal(
    selectRecoverableToken([managed], managedTerminal, now),
    '0123456789abcdef0123456789abcdef'
  );
});
