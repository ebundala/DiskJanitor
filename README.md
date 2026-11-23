# Disk Janitor

A small Avalonia desktop app that finds the usual disk hogs on a developer machine and lets you delete them one at a time, showing what each item is and what depends on it before you delete.

| Category | What's listed | How it's deleted |
|---|---|---|
| Project build folders | `node_modules`, `build`, `dist`, `.next`, `bin`/`obj`, `.dart_tool`, `.gradle`, `target`, `.venv`, `vendor`, … under the project folders | folder removed |
| Docker images | each tag, with containers using it and the Dockerfiles/compose files that reference or built it | `docker rmi` |
| Docker containers | all containers, with their compose project | `docker rm` |
| Docker volumes | named and anonymous volumes, with compose project and attached containers | `docker volume rm` |
| Docker build cache | all build cache | `docker builder prune -af` |
| Package manager caches | npm, pnpm, NuGet, Gradle, pub, cargo, go, pip, … | folder contents removed |
| App & browser caches | VS Code, Chrome, Mesa shaders, thumbnails, … | folder contents removed |
| Trash | home trash and `.Trash-UID` on mounted drives | emptied |
| System (root) | journal (keeps newest 200 MB), apt cache, rotated logs, crash dumps, disabled snap revisions | `journalctl --vacuum-size`, `apt-get clean`, `snap remove --revision` |

Card colours: no stripe = nothing known depends on it, orange = referenced by a project or may hold data, red = in use right now.

## Download

Get `DiskJanitor-linux-x64.tar.gz` from [Releases](https://github.com/ebundala/DiskJanitor/releases). It is a single self-contained binary for 64-bit Linux and does not need .NET installed.

```bash
tar -xzf DiskJanitor-linux-x64.tar.gz
./DiskJanitor            # open the app
./DiskJanitor --report   # print a scan in the terminal, deletes nothing
```

Use **Relaunch as root** inside the app to clean system items (needs pkexec).

## Run from source

```bash
./run.sh                 # normal user
./run-as-root.sh         # as root via sudo (still cleans *your* home)
./run.sh --report        # print a scan in the terminal, deletes nothing
./install-menu-entry.sh  # add "Disk Janitor" to the app menu
```

Inside the app, **Relaunch as root** does the same as `run-as-root.sh` through pkexec.

## Rebuild

```bash
dotnet publish -c Release -o publish
```

Release binary (self-contained, single file):

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o dist
```

## License

[MIT](LICENSE)
