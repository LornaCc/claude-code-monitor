const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { selectLegacyTerminal } = require('./terminal-matcher');

function terminal(name, workingDirectory) {
  return {
    terminal: { name },
    terminalToken: '',
    workingDirectory
  };
}

test('matches a terminal at the project root when the session cwd is deeper', () => {
  const project = path.join(path.parse(process.cwd()).root, 'work', 'project');
  const result = selectLegacyTerminal(
    [terminal('project', project)],
    path.join(project, 'src', 'feature'),
    [project]
  );

  assert.equal(result.terminal.name, 'project');
  assert.equal(result.matchKind, 'sessionWorkingDirectoryDescendant');
});

test('chooses the unique closest terminal ancestor', () => {
  const root = path.join(path.parse(process.cwd()).root, 'work', 'project');
  const source = path.join(root, 'src');
  const result = selectLegacyTerminal(
    [
      terminal('project-root', root),
      terminal('source-root', source)
    ],
    path.join(source, 'feature'),
    [root]
  );

  assert.equal(result.terminal.name, 'source-root');
  assert.equal(result.matchKind, 'sessionWorkingDirectoryDescendant');
});

test('rejects equally close terminal ancestors', () => {
  const project = path.join(path.parse(process.cwd()).root, 'work', 'project');
  const result = selectLegacyTerminal(
    [
      terminal('first', project),
      terminal('second', project)
    ],
    path.join(project, 'src'),
    [project]
  );

  assert.equal(result.terminal, undefined);
  assert.equal(result.matchKind, 'ambiguousSessionWorkingDirectory');
});

test('does not treat a broad directory outside the workspace as a project terminal', () => {
  const root = path.parse(process.cwd()).root;
  const desktop = path.join(root, 'users', 'example', 'desktop');
  const project = path.join(desktop, 'projects', 'project');
  const result = selectLegacyTerminal(
    [
      terminal('desktop', desktop),
      terminal('unrelated', path.join(root, 'other'))
    ],
    path.join(project, 'src'),
    [project]
  );

  assert.equal(result.terminal, undefined);
  assert.equal(result.matchKind, 'noMatch');
});

test('allows a specific unique parent cwd when the VS Code window has no workspace', () => {
  const project = path.join(
    path.parse(process.cwd()).root,
    'users',
    'example',
    'project'
  );
  const result = selectLegacyTerminal(
    [terminal('project', project)],
    path.join(project, 'src'),
    []
  );

  assert.equal(result.terminal.name, 'project');
  assert.equal(result.matchKind, 'sessionWorkingDirectoryDescendant');
});

test('allows a nearby project terminal outside the window workspace', () => {
  const root = path.join(
    path.parse(process.cwd()).root,
    'users',
    'example',
    'projects'
  );
  const terminalDirectory = path.join(root, 'clearMLdemo');
  const result = selectLegacyTerminal(
    [terminal('clearMLdemo', terminalDirectory)],
    path.join(terminalDirectory, 'clearml', 'clearml'),
    [path.join(root, 'fstr_img_tag_manager')]
  );

  assert.equal(result.terminal.name, 'clearMLdemo');
  assert.equal(result.matchKind, 'sessionWorkingDirectoryDescendant');
});

test('keeps exact cwd matching ahead of ancestor matching', () => {
  const project = path.join(path.parse(process.cwd()).root, 'work', 'project');
  const source = path.join(project, 'src');
  const result = selectLegacyTerminal(
    [
      terminal('project-root', project),
      terminal('exact', source)
    ],
    source,
    [project]
  );

  assert.equal(result.terminal.name, 'exact');
  assert.equal(result.matchKind, 'exactWorkingDirectory');
});
