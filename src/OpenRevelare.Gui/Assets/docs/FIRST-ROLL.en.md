# Your first roll: import, edit, export, reopen

Start with a roll you can keep editing and recalculate later. Originals stay untouched; edits live in a `.ncproj` project. Use the User Guide for individual controls and Theory for the algorithms.

## 1. Import one roll

Add the RAW or TIFF files from one roll. The default Path B is for white-light camera copying. Choose Path A only for an RGB narrow-band light source with the three matching reference images. Keep one input family per roll: RAW flat fields for RAW rolls, TIFF flat fields for TIFF rolls.

For a typical import, just add originals and import. Splitting, light-source selection and flat fields live under Advanced import settings. If you select Path A or LCC, the dialog shows which reference is needed and waits until it is ready.

Leave automatic roll analysis enabled initially. Valid TIFF ICC profiles take priority. If the file has no trustworthy declaration, confirm the suggested sRGB or linear interpretation against the scanner's actual output. Colour adjustments cannot fix an incorrect input interpretation.

**Done when:** frames appear in the filmstrip, the current frame is visible and background analysis reports progress. **Recover by:** checking the filename and error in the status bar; try a single original first, then import the roll again. Exported JPEGs are not negative source inputs.

## 2. Let roll analysis finish, then check calibration

The first image is a provisional single-frame conversion. Wait for pooled roll analysis before assessing the final calibration. Check film-base and highlight evidence on the Cineon page.

Film base means clean, unexposed transparent rebate. The highlight endpoint is a scene proxy, not a measurement of the film's physical maximum density. Without visible clean base, an automatic estimate may be inferred from picture content.

For manual base sampling, select an even, unexposed area near the edge of the negative. Avoid text, dust, sprocket holes and the light panel. After changing the light source, device correction or flat field, rerun calibration and check the endpoints.

**Done when:** analysis has finished, the base provenance and confidence make sense, and representative frames have no unexplained overall cast. **Recover by:** checking input interpretation, source borders and reference files, then rerunning automatic roll calibration. It resets calibration endpoints and black/white offsets, so save a project copy before rerunning on an edited roll.

## 3. Reuse calibration, adjust individual frames

Apply a verified representative calibration to the roll if needed. Automatic pooled analysis already shares its calibration, so repeating the sync is unnecessary. Use Display controls for individual white balance, exposure, contrast, saturation and curves.

To transfer a grade, copy Display parameters, tick destination frames and paste to the selection. Check enabled sync groups first. Controls edit the current frame; ticked frames are targets for explicit batch commands.

Orientation, cropping and sprocket masks have separate apply operations. Keep different compositions separate. A successful batch is one undo operation; an unavailable target prevents the whole batch and preserves the selection for retry.

**Done when:** representative frames share a consistent calibration while exposure preserves scene differences. **Recover by:** undoing an incorrect sync immediately, then checking source, targets and sync groups.

## 4. Export one representative frame first

Use an output folder separate from the originals. sRGB JPEG is suitable for sharing; 16-bit TIFF is useful for further editing. HDR, linear DNG and ACEScg masters require a compatible downstream workflow.

Export a representative image with shadows, highlights and neutral areas. Check it in a colour-managed viewer, then confirm format, colour space, dimensions, naming, selected targets and conflict handling before exporting the roll.

Choose a format and size directly: JPEG for sharing, TIFF for further editing, or HEIC, linear DNG and scene-linear ACEScg TIFF when needed. Exports retain the roll's SDR/HDR and rendering space; P3 or Adobe RGB is not silently changed to sRGB. Open the side panel to add film capture details. Scene-linear TIFF excludes finishing adjustments and does not replace archiving originals, project and references.

For SDR-reader compatibility, HDR JPEG includes an SDR primary with the final master's colour and an HDR gain map. HDR HEIC uses BT.2020/PQ; floating-point HDR TIFF is a linear master. Those formats still require a viewer that interprets their colour declarations correctly.

**Done when:** the status reports completion and the output opens with the expected dimensions and colours. **Recover by:** checking folder permissions, free disk space and source availability; keep originals and project intact, fix the cause and retry.

Roll exports show per-frame results. After restarting the app, reopen the same project and choose File → View last batch export results to review or retry failed and pending entries in one step. Completed entries are not exported again. Retries keep the task's settings and destination paths while reading the current originals.

The result entry remains available even if the current frame cannot display. Results identify the original roll, start time and format. If the project, a LUT, a calibration reference or a completed output changed after the task began, the old results remain available for review, but a new roll export is required to avoid mixing colour bases or overwriting changed files.

## 5. Reopen and verify recovery

Let autosave finish before returning to the library or closing. Reopen the roll and check exposure and framing on one edited frame. Projects are saved beside writable originals, with a fallback in the user configuration directory's `rolls` folder.

The status bar independently shows unsaved, saving, saved or failed state. Hover for the project path and last save time; click to retry. A failed close-time save keeps the window open and offers retry, a recovery copy or explicit discard.

When archiving, keep originals, `.ncproj`, flat fields, Path A references and custom LUTs together. A project does not contain the original photographs or bundle every external reference.

Use File → View project dependencies to inspect resource paths, using frames and missing files, and export a JSON manifest. Shared files appear once, including virtual-copy uses; disabled but declared references remain listed. File presence does not validate contents or calibration revisions, so also resolve reference warnings. The manifest includes full local paths; review it before sharing. Exporting a manifest does not copy photographs or references.

Existing relative paths in a project resolve against the `.ncproj` directory, rather than the application's working directory. They remain resolvable when the directory layout moves together. Autosaving after reopening preserves those relative paths.

Choose File → Archive project and originals, then select a location. The app creates a new folder containing copies of the originals, declared calibration files and custom LUTs, plus `project.ncproj` and `checksums.json`. Keep the whole folder when moving or backing up the roll. The archive captures adjustments at the start of the operation while the original project continues saving. Missing or changing resources, cancellation and write failures leave no published partial folder. The SHA-256 checksums can be used to verify the files later.

Missing references preserve the declared Path A/B and LCC enable choice. A persistent notice prevents exporting a degraded result. Use Restore project references to select a new folder; references in different folders can be restored separately. Newly saved projects verify relocated originals using length and sampled fingerprints from the start/end of the file, not full-file checksums. Older projects without fingerprints ask for explicit confirmation of same-name matches.

Recovery replaces only unavailable references and validates candidate flat fields and LUTs before writing the project. Invalid candidates do not overwrite saved declarations. Modified references require explicit recovery/reload or reopening; failed or cancelled validation does not publish a different active LUT. Saving after an intentional original replacement updates its relocation fingerprint.

Removing frames and copies, creating copies, adding and sorting are undoable; removal never deletes originals. LCC enable choice is saved for the whole roll. Older projects use the first frame's flag when the roll flag is absent; only projects with neither flag retain the compatibility default of enabling an available reference.

**Done when:** the frame list, calibration, adjustments and framing return as expected. **Recover by:** checking that you opened the same project and that original/reference paths exist. Resolve a save failure before switching rolls.

## Replacing files during editing

Cache reads and background publication check source revisions. A changed original is decoded again on the next read; results computed while it changed are rejected. If a calibration reference changes, explicitly reload and recalibrate, or reopen the project. The application does not silently replace its colour basis.

An image still on screen may be the last valid preview. After a change or read-failure message, restore or reload the file before sampling or exporting.
