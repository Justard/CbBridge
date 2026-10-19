# Branching and releases (Gitflow)

This repository follows **Gitflow** as described by Atlassian
(<https://www.atlassian.com/git/tutorials/comparing-workflows/gitflow-workflow>), with short-lived branches named
by the kind of work.

```mermaid
gitGraph
    commit id: "1.0.0" tag: "v1.0.0"
    branch develop
    checkout develop
    branch feature/history
    commit id: "history"
    checkout develop
    merge feature/history
    branch bugfix/timeout-label
    commit id: "fix"
    checkout develop
    merge bugfix/timeout-label
    branch release/1.1.0
    commit id: "version 1.1.0"
    checkout master
    merge release/1.1.0 tag: "v1.1.0"
    checkout develop
    merge release/1.1.0
    checkout master
    branch hotfix/1.1.1
    commit id: "urgent fix"
    checkout master
    merge hotfix/1.1.1 tag: "v1.1.1"
    checkout develop
    merge hotfix/1.1.1
```

## Long-lived branches

| Branch | Holds | Changed only by |
|---|---|---|
| `master` | What is released. Every commit on it is a version, tagged `vX.Y.Z`. | Merges from `release/*` and `hotfix/*` |
| `develop` | The next version, integrated and tested. **Default branch**: new work starts here. | Pull requests from `feature/*`, `bugfix/*`, `test/*`, `chore/*`; merges back from `release/*` and `hotfix/*` |

Nobody commits directly to either.

## Short-lived branches

| Prefix | For | From | Pull request into |
|---|---|---|---|
| `feature/` | New functionality — `feature/licensing` | `develop` | `develop` |
| `bugfix/` | A fix for something not yet released — `bugfix/history-paging` | `develop` | `develop` |
| `test/` | Tests only — `test/sqlite-migrations` | `develop` | `develop` |
| `chore/` | Build, CI, docs, dependencies, tidying — `chore/gitflow-setup` | `develop` | `develop` |
| `release/` | Preparing a version — `release/1.1.0` | `develop` | `master`, then back into `develop` |
| `hotfix/` | An urgent fix to a released version — `hotfix/1.1.1` | `master` | `master`, then back into `develop` |

Names: lower case, words joined by `-`, short. One topic per branch. Delete the branch after merging.

## Everyday flow

```bash
git switch develop && git pull
git switch -c feature/short-name
# … commits …
git push -u origin feature/short-name
# open a pull request into develop; the pipeline must be green; review; merge
```

## Making a release

1. `git switch -c release/1.1.0 develop`
2. Set `<VersionPrefix>1.1.0</VersionPrefix>` in `Directory.Build.props`; only fixes, docs and release notes from
   now on. Push; the pipeline runs on `release/*`.
3. Pull request `release/1.1.0` → `master`. After merging, tag the merge commit:
   `git switch master && git pull && git tag -a v1.1.0 -m "1.1.0" && git push origin v1.1.0`
4. The tag starts the **release** pipeline: build, tests, ready-to-run downloads for Windows and Linux, attached to
   a GitHub release `v1.1.0`.
5. Merge `release/1.1.0` back into `develop` (pull request), then delete the release branch.

**Hotfix:** the same from `master`: `hotfix/1.1.1` → fix → version `1.1.1` → PR into `master` → tag `v1.1.1`
→ PR into `develop`.

## Versions

`MAJOR.MINOR.PATCH` (semantic versioning): MAJOR for changes that break settings, the framework contract or the
API; MINOR for new features; PATCH for fixes. The version lives once, in `Directory.Build.props`, and shows in the
app and in `/api/v1/status`.

## Commit messages

First line: what changes, imperative, under ~70 characters — "Add licence check at startup". Blank line, then
why, if it isn't obvious.
