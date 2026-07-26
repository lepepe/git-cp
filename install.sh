#!/usr/bin/env bash
# Installs git-cp for the current user on Linux or macOS.
#
# Downloads the matching release binary from GitHub Releases and places it
# in ~/.local/bin (no sudo required). Once installed:
#   git-cp        works directly
#   git cp        works via git's PATH lookup
#
# One-liner install:
#   curl -fsSL https://raw.githubusercontent.com/lepepe/git-cp/main/install.sh | bash

set -euo pipefail

REPO="lepepe/git-cp" # <-- update before publishing
INSTALL_DIR="${HOME}/.local/bin"
BIN_PATH="${INSTALL_DIR}/git-cp"

case "$(uname -s)" in
  Linux)
    ASSET_NAME="git-cp-linux-x64"
    ;;
  Darwin)
    case "$(uname -m)" in
      arm64)  ASSET_NAME="git-cp-osx-arm64" ;;
      x86_64) ASSET_NAME="git-cp-osx-x64" ;;
      *)
        echo "Unsupported macOS architecture: $(uname -m)" >&2
        exit 1
        ;;
    esac
    ;;
  *)
    echo "Unsupported OS: $(uname -s). On Windows, use install.ps1 instead." >&2
    exit 1
    ;;
esac

echo "Fetching latest release info..."

DOWNLOAD_URL=$(
  curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" |
    python3 -c "
import sys, json
data = json.load(sys.stdin)
assets = data.get('assets', [])
match = next((a['browser_download_url'] for a in assets if a['name'] == '${ASSET_NAME}'), None)
if not match:
    raise SystemExit('Asset ${ASSET_NAME} not found in latest release')
print(match)
"
)

VERSION=$(
  curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" |
    python3 -c "import sys,json; print(json.load(sys.stdin)['tag_name'])"
)

echo "Downloading git-cp ${VERSION}..."

mkdir -p "${INSTALL_DIR}"
curl -fsSL "${DOWNLOAD_URL}" -o "${BIN_PATH}"
chmod +x "${BIN_PATH}"

echo "Installed to: ${BIN_PATH}"

# Remind the user to add ~/.local/bin to PATH if it isn't already there
if [[ ":${PATH}:" != *":${INSTALL_DIR}:"* ]]; then
  echo ""
  echo "~/.local/bin is not in your PATH. Add the following line to your shell profile"
  echo "(~/.bashrc, ~/.zshrc, etc.) and restart your terminal:"
  echo ""
  echo '  export PATH="$HOME/.local/bin:$PATH"'
fi

echo ""
echo "Done! Run 'git cp' (or 'git-cp') from inside any git repository."
