# Contributing to universal-cam

Thanks for contributing.

## Ground Rules

- Be respectful and constructive in discussion and review.
- Keep pull requests focused and reasonably small.
- Add or update tests when behavior changes.
- Update documentation for user-facing or protocol changes.

## Development Setup

### Windows side

1. Install .NET 8 SDK and Visual Studio 2022 Build Tools.
2. Run:
   - dotnet restore universal-cam.sln
   - dotnet build universal-cam.sln --configuration Debug
   - dotnet test universal-cam.sln

### iOS side

1. Open ios/UniversalCamPhone.xcodeproj in Xcode 15+.
2. Build and run on a real iPhone for transport/camera testing.

## Branch and Commit Workflow

- Branch from master using a descriptive name:
  - feat/short-description
  - fix/short-description
  - docs/short-description
- Commit messages should be imperative and clear.
- Rebase/sync with latest master before opening a PR.

## Pull Request Checklist

- Code builds locally.
- Relevant tests pass.
- New behavior is documented.
- No unrelated refactors mixed into the change.
- PR description explains what changed and why.

## Reporting Issues

When filing a bug, include:

- Expected behavior
- Actual behavior
- Reproduction steps
- Environment details (Windows version, iOS version, device model)
- Logs/screenshots when available

## Security Reports

Please do not open public issues for vulnerabilities.

Email maintainers privately and include:

- Impact summary
- Reproduction details
- Suggested mitigation (if known)
