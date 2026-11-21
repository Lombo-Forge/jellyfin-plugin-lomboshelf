#!/bin/sh
# Real-Jellyfin check of the plugin, in a THROWAWAY Jellyfin on 127.0.0.1:18096. Never point it at your own server.
# Needs docker, gh (to fetch File Transformation) and python3. Everything lives in ./.scratch-jf (delete it afterwards).
#   JELLYFIN_IMAGE   default jellyfin/jellyfin:12.4     FT_RELEASE  default 3.0.3.0     FT_ASSET  default Release-12.4.0.zip
set -eu
cd "$(dirname "$0")/../.."
root=$PWD; s=$root/.scratch-jf; img=${JELLYFIN_IMAGE:-jellyfin/jellyfin:12.4}
rm -rf "$s"; mkdir -p "$s/config/plugins/LomboShelf_0.1.0.0" "$s/config/plugins/FileTransformation" "$s/cache" "$s/media/movies" "$s/out" "$s/shots"
echo "== build the plugin"
cat > "$s/publish.sh" <<'B'
mkdir -p /tmp/w && cd /w && tar --exclude=node_modules --exclude=.scratch-jf -cf - . | tar -C /tmp/w -xf - && cd /tmp/w/plugin/Jellyfin.Plugin.LomboShelf
dotnet publish -c Release -o /out 2>&1 | tail -1
B
docker run --rm --name lf-dotnet-real --cpus 2 --memory 2g --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  -v "$root:/w:ro" -v "$s/out:/out" -v "$s/publish.sh:/b.sh:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh /b.sh
cp "$s/out/Jellyfin.Plugin.LomboShelf.dll" "$s/config/plugins/LomboShelf_0.1.0.0/"
echo "== File Transformation ${FT_RELEASE:-3.0.3.0} (third-party plugin, installed in the throwaway server only)"
(cd "$s" && gh release download "${FT_RELEASE:-3.0.3.0}" --repo IAmParadox27/jellyfin-plugin-file-transformation --pattern "${FT_ASSET:-Release-12.4.0.zip}" -D ft --clobber)
python3 -c "import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$s/ft/${FT_ASSET:-Release-12.4.0.zip}" "$s/config/plugins/FileTransformation"
echo "== three tiny test movies"
for n in "Space Test (2020)" "Ocean Test (2021)" "City Test (2022)"; do
  mkdir -p "$s/media/movies/$n"
  docker run --rm --name lf-jf-ffmpeg --cpus 1 --memory 512m --user "$(id -u):$(id -g)" -v "$s/media:/media" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$img" \
    -v error -f lavfi -i "testsrc2=size=640x360:rate=24:duration=3" -f lavfi -i "sine=frequency=440:duration=3" -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "/media/movies/$n/$n.mkv"
done
echo "== start a throwaway Jellyfin"
docker rm -f lf-jf-real >/dev/null 2>&1 || true
docker run -d --name lf-jf-real --cpus 2 --memory 2g --user "$(id -u):$(id -g)" -e HOME=/tmp -p 127.0.0.1:18096:8096 \
  -v "$s/config:/config" -v "$s/cache:/cache" -v "$s/media:/media:ro" "$img" >/dev/null
trap 'docker rm -f lf-jf-real >/dev/null 2>&1 || true' EXIT
until curl -sf -o /dev/null http://127.0.0.1:18096/Startup/Configuration; do sleep 3; done
python3 test/real-jellyfin/wizard.py
docker logs lf-jf-real 2>&1 | grep -E "Loaded plugin: (LomboShelf|File Transformation)|index.html transformation registered" | cut -c1-160
python3 test/real-jellyfin/check.py
ip=$(docker inspect lf-jf-real --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')
echo "== browser (Playwright 1.49.1)"
docker run --rm --name lf-jf-browser --cpus 2 --memory 2g --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$root/test/real-jellyfin:/w:ro" -v "$s/shots:/shots" mcr.microsoft.com/playwright/python:v1.49.1-noble \
  sh -c "pip install -q --target /tmp/pw playwright==1.49.1 2>&1 | grep -v -i notice | tail -2; PYTHONPATH=/tmp/pw python /w/browser.py http://$ip:8096 /shots"
echo "screenshots: $s/shots"
