# VR180 Viewer

A stereo photo viewer for **Meta Quest 3**, built in Unity, aimed at getting the sharpest possible view of full-resolution VR180 stills, such as those from the Kandao QooCam 3 Ultra.

Most viewers draw the photo into the headset's eye buffers, so the image is resampled twice before it reaches your eyes. This viewer hands each eye's half of the photo straight to the Quest's compositor as a stereo equirectangular layer, so it is resampled only once, at full resolution. In side-by-side comparisons it is noticeably sharper than the commercial viewers.

With a QooCam 3 Ultra modified for VR180 (lenses side by side), it can also **import photos straight from the camera over USB-C** and show the camera's unprocessed dual-fisheye images, converting them in the headset with the same geometry as QooCam Studio, using your camera's own Studio calibration. No PC needed between taking a photo and viewing it.

> **Status:** a hobby project that does everything its author needs. Shared so other camera owners can try it, report problems and improve it. Expect rough edges.

## Features

- **Maximum sharpness:** each eye shown at full source resolution through its own compositor layer.
- **Soft dead zone:** outside the 180° image, a mirrored, progressively blurred fill instead of a hard black edge, with no "vortex" when you look straight up or down.
- **Gentle transitions:** the old photo melts to its average colour and the new one sharpens in, with no flash to black.
- **Thumbnail picker:** a scrolling grid of stereo thumbnails (they show in 3D in the headset), folder tiles with a cover photo and photo counts, and the current photo highlighted. Thumbnails are made in the background the first time you open a folder and kept for next time.
- **Zoom (0.5× to 2×)** with either thumbstick. Zooming in uses a telephoto-style projection, so straight lines stay straight and the horizon stays level, wherever you zoom. On the Quest the zoomed view is re-made at full sharpness a moment after you let go of the stick.
- **Tilted photos:** photos taken pointing up or down are shown level, using the camera's own tilt sensor, with nothing cut off. A brief arrow points to the photo's centre when it loads if it is well above or below where you are looking. Levelling can be switched off for any one photo.
- **Image adjustments** per photo: brightness, contrast, saturation, convergence; plus global sharpness, roll compensation and head-turn parallax.
- **Favourites:** mark photos with the trigger, view all favourites (in name order or shuffled; a shuffle carries on where it left off next time), tidy a folder by deleting its non-favourites, and (on PC) copy favourites to a folder ready to transfer to the Quest.
- **Flat 2D export:** frame part of the photo with a 16:9 guide and save it as an ordinary JPEG to share with people who don't have a headset.
- **One-handed use:** everything except flat export works with the right controller alone.
- **Roll compensation:** when you look down and to the side, the stereo pair turns with your head to reduce vertical mismatch (adjustable, 0 to 100%; 70% by default).
- **Head-turn parallax (experimental, off by default):** a small amount of parallax as you turn your head, as if the scene were at a set distance.
- **Stays put:** photos keep the same position as you step through them; hold the Quest button (or the right thumbstick click) to recentre. The view recovers by itself after the headset sleeps.
- **Import from camera:** plug a QooCam 3 Ultra into the Quest's USB-C port and copy its new JPEGs across from the picker (about 35 MB/s; photos already imported are skipped).
- **Raw QooCam photos in the headset:** the camera's dual-fisheye JPEGs are converted as they're shown (the files aren't changed), using the camera's Studio calibration and levelled from the camera's own gravity reading, as QooCam Studio does. The other lens's barrel, which the modified camera's left lens can see at the edge of its picture, is patched out.
- **QooCam Studio photos can be levelled too**, with a small script that copies the camera's tilt reading into Studio's exports (see below).

## Requirements

- Meta Quest 3 (other Quest models are untested; the two full-resolution layers use about 1.1 GB of memory).
- Developer Mode enabled on the headset, to install an APK.
- VR180 photos as **side-by-side equirectangular JPEGs**. GPano (Google Photo Sphere) metadata is read if present; without it, a JPEG is assumed to be 180° × 180° side-by-side. Developed with QooCam 3 Ultra stills (13,888 × 6,944).
- Or **unprocessed QooCam 3 Ultra JPEGs** (two fisheye circles side by side) from a camera modified for VR180, with its calibration file (see below).

## Installing the APK

1. Download `VR180Viewer.apk` from the [Releases](../../releases) page. Its SHA-256 checksum is listed there so you can check the download.
2. Install it with [SideQuest](https://sidequestvr.com/), or with adb:
   ```
   adb install -r VR180Viewer.apk
   ```
   Installing a new version over an old one keeps your favourites, adjustments and thumbnails.
3. Start the viewer (in your app library under **Unknown Sources**). The first time, it asks for **All files access**, so it can read your photos: choose **Open settings**, allow it, then close the Settings window to return to the viewer.
   If the viewer doesn't respond after that, quit and restart it. Alternatively, grant the permission from a PC with adb:
   ```
   adb shell appops set --uid com.grahamwell.vr180viewer MANAGE_EXTERNAL_STORAGE allow
   ```

## Where to put your photos

Copy them into the Quest's **Pictures** folder, in any subfolder you like (connect the Quest to your PC by USB and open *Quest 3 › Internal shared storage › Pictures*). This is the same layout ImmerGallery uses, so both apps can share the same photos.

Flat exports are saved to *Pictures/VR180 Flat*.

## Controls

| Control | Action |
|---|---|
| Either thumbstick left / right | Previous / next photo |
| Either thumbstick up / down | Zoom in / out (pauses at 1× on the way through; resets for each photo). In the picker: scroll |
| Right **A** | Open or close the picker (also has the Exit button) |
| Right **B** | Open or close the adjustments panel |
| Right trigger | Mark or unmark a favourite (select and drag in the picker and panel) |
| Hold right thumbstick click, or hold the Quest button | Recentre the view |
| Left trigger | Flat export: enter framing mode, then press again to save |
| Left thumbstick (framing) | Zoom the framing guide |
| Left grip (framing) | Cancel the flat export |

A favourite is confirmed with a sound and a buzz on the right controller: a rising chime when added, a falling one when removed.

In the Unity Editor: arrow keys to step through photos, M for the picker, A for the adjustments panel, space to mark a favourite, F for flat export, T to switch zoom style.

Per-photo adjustments, favourites and thumbnails are stored inside the app's own data folder, so uninstalling the app erases them.

> **Careful:** "Tidy" in the picker permanently deletes the non-favourite photos in that folder (after a confirmation page).

## Importing from a QooCam 3 Ultra (modified for VR180)

1. **Calibration, once per camera.** Calibrate the camera in QooCam Studio on your PC as usual. Studio saves the result as
   `%APPDATA%\QooCamStudio\calibration\<serial>\<serial>_refined.xml`.
   Copy that file into **Pictures/QooCam Calibration** on the Quest (the folder appears after the viewer's first run). Several cameras can share the folder: each photo is matched to its camera by the serial number in its EXIF data. On a PC the viewer reads Studio's folder directly.
   Without the file, the viewer uses a built-in calibration from another camera and shows a "Camera not calibrated" notice; the two eyes may then not line up.
2. **Import.** Plug the camera into the Quest's USB-C port and switch it on. Press **A** for the picker; at the top level choose **Import from camera**, allow USB access when Android asks, then import the newest 5 (as a test) or all new photos. They go into *Pictures/QooCam Import*. JPEGs only: DNG files and the camera's small preview images stay on the camera.
3. **View.** Choose the folder in the picker as usual. Each photo is converted from fisheye as it loads; the files themselves aren't changed.

The conversion matches QooCam Studio's geometry to within a pixel, including levelling the horizon from the camera's gravity sensor (untick **Level the horizon** in the adjustments panel to see a photo as the camera was held). Studio's colour and tone processing, and its lens-shading correction, aren't reproduced: Studio usually works from the DNG.

If the camera isn't detected, unplug and replug it, or leave the app and come back: the connection can be hit and miss.

## Levelling photos exported from QooCam Studio

QooCam Studio leaves out the camera's tilt reading when it exports a photo, so the viewer can't level it. If you'd rather let the viewer do the levelling (it keeps everything above and below the horizon, where Studio's Horizon Correction crops), use the script in `Tools`:

1. In QooCam Studio, export with **Horizon Correction off**.
2. Run (Python 3.8 or later, nothing else needed):
   ```
   python Tools/qoocam_tag_studio.py "<Studio output folder>" "<folder with the camera's original JPG or DNG files>"
   ```
   Add `--dry-run` to see what it would do, or `--recursive` to include subfolders.

For each Studio JPEG it finds the camera's original with the same base name and adds the tilt reading and serial number to the Studio file as a small JPEG comment. The picture itself isn't re-compressed or changed, and running it twice is harmless. Don't use it on photos Studio has already levelled: they would be turned twice (if that happens, untick **Level the horizon** for that photo).

## Viewing on a PC (Quest Link)

The viewer also runs in the Unity Editor over Quest Link, which is handy for reviewing photos stored on the PC. Link has no stereo equirect layer, so the photo is drawn at full resolution on the sphere instead. For the sharpest picture:

- In the Meta Quest Link app, set the **Render resolution** to about 1.2×.
- On the **Viewer** object, leave **PC Render Scale** at 2 (lower it if the frame rate suffers).
- In the adjustments panel, set **Sharpness** to 0 (it is remembered separately on the PC and the Quest).

Set **Pc Folder** on the Viewer object to your photo folder. On the PC, the picker can also copy your favourites into a folder (**Pc Favourites Folder**) ready to transfer to the Quest.

## Building from source

1. Install **Unity 6000.3.9f1** (6.3 LTS) from Unity Hub, with **Android Build Support** (including OpenJDK and the Android SDK & NDK).
2. Get the project: the green **Code** button above, then *Download ZIP* (or clone it with Git).
3. In Unity Hub choose **Add › Add project from disk** and pick the folder. The first opening takes several minutes while Unity downloads packages and rebuilds its Library folder.
4. Open **Assets/Scenes/viewer.unity**.
5. For PC / Quest Link use, select the **Viewer** object and set **Pc Folder** to your own photo folder.
6. **File › Build Profiles › Android**, switch platform if asked, connect the Quest by USB and choose **Build And Run**.

It's worth setting your own package name (*Project Settings › Player › Android › Other Settings › Package Name*) before building, and using it in the adb command above. A different package name installs as a separate app.

### Package versions matter

`Packages/packages-lock.json` pins the exact versions the viewer was developed with, notably **OpenXR 1.16.1** and **XR Composition Layers 2.6.1**. The custom layer handler works around bugs in those versions, so please don't upgrade them casually. If you do, read the notes below first.

## How it works

The code is all in `Assets/VR180`, apart from two small Java plugins in `Assets/Plugins/Android`.

| File | Purpose |
|---|---|
| `VR180Viewer.cs` | Main controller: lists and decodes photos, reads metadata, extracts each eye, drives the two compositor layers, zoom, transitions and input. |
| `VR180StereoEquirectHandler.cs` | Replaces Unity's OpenXR equirect layer handler on the Quest (when `XR_KHR_composition_layer_equirect2` is available). |
| `VR180LayerSettingsFeature.cs` | OpenXR feature that enables `XR_FB_composition_layer_settings` (compositor sharpening while zoomed). |
| `VR180StereoEquirect.shader` | The head-following sphere: preview, dead-zone fill, zoom and transitions. |
| `VR180EyeExtract.shader` | Copies one eye at one source pixel per output pixel, with a feathered edge, baked-in adjustments and (when zoomed) the telephoto zoom. |
| `Resources/VR180FillBake.shader` | Bakes the dead-zone fill into small octahedral maps (no vortex at the poles). |
| `VR180PhotoPicker.cs` | In-headset thumbnail picker, favourites, shuffle, tidy, copy and import pages. |
| `VR180Thumbnails.cs`, `Resources/VR180Thumbnail.shader`, `Resources/VR180StereoThumb.shader`, `Plugins/Android/ThumbDecoder.java` | Making, caching and showing stereo thumbnails (fast reduced-size JPEG decode on the Quest). |
| `VR180AdjustPanel.cs`, `VR180Adjustments.cs` | Adjustments panel and per-photo settings. |
| `VR180Favourites.cs`, `VR180FavouriteBadge.cs` | Favourites list, sounds and star. |
| `VR180FlatExport.cs`, `Resources/VR180FlatExport.shader` | Flat 2D JPEG export. |
| `VR180LoadingIndicator.cs` | Loading dots while a photo decodes. |
| `VR180TiltHint.cs` | The brief arrow pointing to a tilted photo's centre. |
| `VR180Notice.cs` | Short notices in the headset, such as a missing calibration file. |
| `Editor/VR180AndroidManifest.cs` | Adds the All files access permission to the Android manifest. |
| `VR180CameraImport.cs`, `Plugins/Android/CameraImport.java` | Import from a camera over USB (Android MTP). |
| `VR180Fisheye.cs`, `VR180Fisheye.hlsl`, `Resources/VR180FisheyeToEquirect.shader` | Raw dual-fisheye conversion: calibration files, tilt readings, the lens model and the lens-barrel patch. |
| `Tools/qoocam_tag_studio.py` | Copies the camera's tilt reading into QooCam Studio exports (runs on the PC, not part of the app). |

Notes for developers, found the hard way:

- **Two layers, not one.** OpenXR 1.16.1's equirect handler sends the left texture to both eyes (per-eye targeting arrives in 1.18), so the viewer uses one layer per eye with its own handler.
- **Lower vertical angle.** Composition Layers 2.6 measures it from the equator, but OpenXR 1.16.1 negates it, giving a zero-height band. The custom handler passes angles through unchanged.
- **Write-once swapchains.** Unity's handler copies the source into the swapchain every frame; this one writes each swapchain image once and then frees the eye textures (about 380 MB saved). After the headset sleeps the swapchains are lost, so the viewer re-decodes the photo.
- **Quest Link** has no `equirect2`, so on PC the layer is off and the photo is shown on the sphere at full resolution instead.
- **Levelling by turning, not resampling.** A tilted photo is extracted in the camera's own frame and the sphere and layers are turned to level it, so nothing beyond 90° from the horizon is lost.
- **Telephoto zoom on the Quest.** An equirect layer can only scale longitude and latitude, which bends the horizon when zoomed off-centre. So while zooming the layer steps aside (the softer sphere shows the zoom), and once the stick has been released for half a second the eye images are re-made with the zoom baked in (about 2.5 s).

If you move to OpenXR 1.18 or later, these workarounds may become unnecessary or wrong. Try it in a copy of the project.

## Known limitations

- Photos without GPano metadata are assumed to be 180° × 180° side-by-side, so flat photos or 360° images display wrongly.
- Raw QooCam conversion supports the QooCam 3 Ultra's 13,888 × 6,944 stills (other sizes are scaled from the calibration, untested). Lens shading and Studio's colour processing aren't applied. The lens-barrel patch is shaped for the author's modified camera; a camera modified differently may need it adjusted (`VR180Fisheye.cs`).
- On the Quest, each change of zoom takes a couple of seconds to become sharp.
- Thumbnails are only made while the picker is open, and don't show per-photo adjustments.
- Top-bottom stereo shows in mono.
- No video support (yet).
- Flat export over Quest Link is unreliable.

## Contributing

Bug reports, test results from other cameras and pull requests are all welcome. Please open an [issue](../../issues) describing your headset, camera and what happened.

## Licence

The project's own code is released under the [MIT Licence](LICENSE). Unity template, sample and package assets included in the project remain under their own licences.
