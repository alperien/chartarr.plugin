#!/bin/sh
# fetches the lidarr source this plugin builds against, at the pinned commit
set -e
sha=$(cat lidarr-version.txt)
if [ ! -d Submodules/Lidarr/.git ]; then
    git clone https://github.com/Lidarr/Lidarr Submodules/Lidarr
fi
git -C Submodules/Lidarr fetch --depth 1 origin "$sha"
git -C Submodules/Lidarr checkout "$sha"
echo "lidarr source ready at $sha"
