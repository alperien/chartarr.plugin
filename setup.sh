#!/bin/sh
# fetches the lidarr source this plugin builds against, at the pinned commit
set -e
sha=$(cat lidarr-version.txt)
if [ ! -d Submodules/Lidarr/.git ]; then
    git init -q Submodules/Lidarr
    git -C Submodules/Lidarr remote add origin https://github.com/Lidarr/Lidarr
fi
git -C Submodules/Lidarr fetch --depth 1 origin "$sha"
git -C Submodules/Lidarr checkout -q FETCH_HEAD
echo "lidarr source ready at $sha"
