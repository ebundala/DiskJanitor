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

All builds are self-contained 64-bit Linux binaries and do not need .NET installed. Files are on the [Releases](https://github.com/ebundala/DiskJanitor/releases) page.

| Distro | Install |
|---|---|
| Debian / Ubuntu | download `diskjanitor_<version>_amd64.deb`, then `sudo apt install ./diskjanitor_<version>_amd64.deb` |
| Arch | `yay -S diskjanitor-bin` (AUR) |
| Any | download `DiskJanitor-x86_64.AppImage`, `chmod +x` it and run it |
| Any | extract `DiskJanitor-linux-x64.tar.gz` and run `./DiskJanitor` |

Add `--report` to print a scan in the terminal without deleting anything.

Use **Relaunch as root** inside the app to clean system items (needs pkexec).

### WSL

Disk Janitor runs in WSL2. On Windows 11 and recent Windows 10 the window opens through WSLg; install the `.deb` or use the tarball. If the AppImage complains about FUSE, run it with `--appimage-extract-and-run`.

- It cleans the Linux side only (your WSL home and the distro's caches), not Windows folders.
- WSL stores each distro in a virtual disk (`ext4.vhdx`) that does not shrink by itself, and Docker Desktop's data is in a similar file. After cleaning, compact the disk from PowerShell to give the space back to Windows, for example `wsl --manage <distro> --set-sparse true`, or run `wsl --shutdown` and then `Optimize-VHD` (Hyper-V) or `diskpart`'s `compact vdisk`.
- **Relaunch as root**, journal cleanup and snap revisions need systemd: set `[boot] systemd=true` in `/etc/wsl.conf` (the default on new Ubuntu installs).

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

Release packages (tarball, .deb, AppImage) into `dist/`:

```bash
packaging/build-packages.sh 1.2.3
```

## Releasing

Push a version tag; [.github/workflows/release.yml](.github/workflows/release.yml) builds the packages, publishes the GitHub release and updates the AUR package.

```bash
git tag -a v1.2.3 -m "Disk Janitor v1.2.3" && git push origin v1.2.3
```

The AUR push needs an `AUR_SSH_PRIVATE_KEY` repository secret holding a key registered on the AUR account that owns `diskjanitor-bin`. Without it, the workflow still builds and checks the Arch package but does not push it.

## License

[MIT](LICENSE)
