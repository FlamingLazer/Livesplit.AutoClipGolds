# Put Auto Clip Golds on GitHub

1. Create a repository named `Auto-Clip-Golds` in your GitHub account.
2. Upload this folder's contents to the repository root. `README.md`, `src`, `scripts`, `tests`, and `dist` should sit directly in the root, rather than inside another folder.
3. Commit the upload. GitHub will display `README.md` on the repository page.
4. Create a release tagged `v0.9.1`, titled **Auto Clip Golds v0.9.1 (Preview)**.
5. Attach `LiveSplit.GoldClip.dll` from `dist` as a release asset, or attach the separately prepared release ZIP.
6. Preview the README and follow its setup instructions on a clean installation before announcing the release.

Suggested repository description:

> Automatically save LiveSplit golds or completed runs with the OBS replay buffer. Configurable padding, undo cleanup, and FFmpeg stream-copy trimming.

Suggested release notes:

> First public preview of Auto Clip Golds for desktop LiveSplit on Windows. Capture gold segments or completed runs using OBS WebSocket 5.x. Includes configurable padding, offset-aware gold filenames, FFmpeg detection, and clip removal on undo. See the README for setup and keyframe-trimming limitations.

Only the files in this prepared folder belong in the repository. Do not upload personal LiveSplit layouts, split files, passwords, logs, recordings, or old DLL backups.

No license has been selected for your code in this package. Choose the license you want before describing the project as open source. The external projects linked in the README retain their own licenses.
