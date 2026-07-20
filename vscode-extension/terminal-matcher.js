const path = require('path');

function selectLegacyTerminal(snapshots, requestedWorkingDirectory, workspaceFolders) {
  const requestedDirectory = normalizePath(requestedWorkingDirectory);
  const normalizedSnapshots = snapshots.map((snapshot) => ({
    ...snapshot,
    workingDirectory: normalizePath(snapshot.workingDirectory)
  }));
  const normalizedWorkspaceFolders = (workspaceFolders || [])
    .map(normalizePath)
    .filter(Boolean);

  if (requestedDirectory) {
    const exactMatches = normalizedSnapshots.filter(
      (snapshot) => snapshot.workingDirectory === requestedDirectory
    );
    if (exactMatches.length === 1) {
      return matched(
        exactMatches[0],
        'exactWorkingDirectory',
        'A single legacy terminal had the exact requested working directory.'
      );
    }
    if (exactMatches.length > 1) {
      return ambiguous(
        'ambiguousExactWorkingDirectory',
        `${exactMatches.length} legacy terminals had the exact requested working directory.`
      );
    }

    const ancestorMatches = normalizedSnapshots.filter(
      (snapshot) =>
        snapshot.workingDirectory
        && isDescendant(requestedDirectory, snapshot.workingDirectory)
        && isSafeTerminalAncestor(
          snapshot.workingDirectory,
          requestedDirectory,
          normalizedWorkspaceFolders
        )
    );
    const closestAncestors = closestMatches(
      ancestorMatches,
      (snapshot) => pathDistance(requestedDirectory, snapshot.workingDirectory)
    );
    if (closestAncestors.length === 1) {
      return matched(
        closestAncestors[0],
        'sessionWorkingDirectoryDescendant',
        'The session working directory was inside a single closest legacy terminal directory.'
      );
    }
    if (closestAncestors.length > 1) {
      return ambiguous(
        'ambiguousSessionWorkingDirectory',
        `${closestAncestors.length} equally close legacy terminal directories contained the session working directory.`
      );
    }

    if (isSpecificProjectPath(requestedDirectory)) {
      const descendantMatches = normalizedSnapshots.filter(
        (snapshot) =>
          snapshot.workingDirectory
          && isDescendant(snapshot.workingDirectory, requestedDirectory)
      );
      const closestDescendants = closestMatches(
        descendantMatches,
        (snapshot) => pathDistance(snapshot.workingDirectory, requestedDirectory)
      );
      if (closestDescendants.length === 1) {
        return matched(
          closestDescendants[0],
          'workingDirectoryDescendant',
          'A single closest legacy terminal was inside the requested project directory.'
        );
      }
      if (closestDescendants.length > 1) {
        return ambiguous(
          'ambiguousWorkingDirectory',
          `${closestDescendants.length} equally close legacy terminals were inside the requested project directory.`
        );
      }
    }
  }

  const workspaceMatches = normalizedWorkspaceFolders.some(
    (workspaceDirectory) =>
      requestedDirectory === workspaceDirectory
      || isDescendant(requestedDirectory, workspaceDirectory)
  );
  if (workspaceMatches && normalizedSnapshots.length === 1) {
    return matched(
      normalizedSnapshots[0],
      'singleWorkspaceTerminal',
      'The selected workspace had exactly one terminal.'
    );
  }

  return {
    matchKind: 'noMatch',
    reason: 'No unique legacy terminal matched the requested working directory.'
  };
}

function matched(snapshot, matchKind, reason) {
  return {
    terminal: snapshot.terminal,
    terminalToken: snapshot.terminalToken,
    matchKind,
    reason
  };
}

function ambiguous(matchKind, reason) {
  return {
    matchKind,
    reason: `${reason} Use CC Monitor: Migrate Active Terminal to create a tokenized terminal.`
  };
}

function closestMatches(candidates, getDistance) {
  if (candidates.length < 2) {
    return candidates;
  }

  const ranked = candidates
    .map((candidate) => ({ candidate, distance: getDistance(candidate) }))
    .sort((left, right) => left.distance - right.distance);
  const closestDistance = ranked[0].distance;
  return ranked
    .filter((item) => item.distance === closestDistance)
    .map((item) => item.candidate);
}

function isSafeTerminalAncestor(terminalDirectory, requestedDirectory, workspaceFolders) {
  const sameWorkspace = workspaceFolders.some((workspaceDirectory) =>
    isSameOrDescendant(terminalDirectory, workspaceDirectory)
    && isSameOrDescendant(requestedDirectory, workspaceDirectory));
  if (sameWorkspace) {
    return true;
  }

  return isSpecificProjectPath(terminalDirectory)
    && pathDistance(requestedDirectory, terminalDirectory) <= 2;
}

function isSameOrDescendant(child, parent) {
  return child === parent || isDescendant(child, parent);
}

function pathDistance(child, parent) {
  if (!isDescendant(child, parent)) {
    return Number.POSITIVE_INFINITY;
  }

  return child
    .slice(parent.length + 1)
    .split(/[\\/]+/)
    .filter(Boolean)
    .length;
}

function normalizePath(value) {
  if (!value || typeof value !== 'string') {
    return '';
  }
  return path.resolve(value).replace(/[\\/]+$/, '').toLowerCase();
}

function isDescendant(child, parent) {
  return Boolean(child && parent && child !== parent && child.startsWith(`${parent}${path.sep}`));
}

function isSpecificProjectPath(value) {
  const parsed = path.parse(value);
  const relative = value.slice(parsed.root.length);
  return relative.split(/[\\/]+/).filter(Boolean).length >= 3;
}

module.exports = {
  normalizePath,
  selectLegacyTerminal
};
