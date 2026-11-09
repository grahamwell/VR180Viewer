# VR180 Viewer

A stereo photo viewer for **Meta Quest 3**, built in Unity, aimed at getting the sharpest possible view of full-resolution VR180 stills, such as those from the Kandao QooCam 3 Ultra.

Most viewers draw the photo into the headset's eye buffers, so the image is resampled twice before it reaches your eyes. This viewer hands each eye's half of the photo straight to the Quest's compositor as a stereo equirectangular layer, so it is resampled only once, at full resolution. In side-by-side comparisons it is noticeably sharper than the commercial viewers.

With a QooCam 3 Ultra modified for VR180 (lenses side by side), it can also **import photos straight from the camera over USB-C** and show the camera's unprocessed dual-fisheye images, converting them in the headset with the same geometry as QooCam Studio, using your camera's own Studio calibration. No PC needed between taking a photo and viewing it.

> **Status:** a hobby project that does everything its author needs. Shared so other camera owners can try it, report problems and improve it. Expect rough edges.

## Features

- **Maximum sharpness:** each eye shown at full source resolution through its own compositor layer.
- **Soft dead zone:** outside the 180° image, a mirrored, progressively blurred fill instead of a hard black edge.
- **Gentle transitions:** the old photo melts to its average colour and the new one sharpens in, with no flash to black.
- **Folder picker** in the headset, with photo counts for each folder and its subfolders.
- **Image adjustments** per photo: brightness, contrast, saturation, convergence; plus a global sharpness setting.
- **Favourites:** mark photos with the trigger, view all favourites, tidy a folder by deleting its non-favourites, and (on PC) copy favourites to a folder ready to transfer to the Quest.
- **Flat 2D export:** frame part of the photo with a 16:9 guide and save it as an ordinary JPEG to share with people who don't have a headset. Verticals stay straight, like a shift lens.
- **One-handed use:** everything except flat export works with the right controller alone.
- **Roll compensation (experimental, off by default):** turns the stereo pair with your head when you tilt it, to reduce vertical mismatch.
- **Import from camera:** plug a QooCam 3 Ultra into the Quest's USB-C port and copy its new JPEGs across from the picker (about 35 MB/s; photos already imported are skipped).
- **Raw QooCam photos in the headset:** the camera's dual-fisheye JPEGs are converted as they're shown (the files aren't changed), using the camera's Studio calibration and levelled from the camera's own gravity reading, as QooCam Studio does.

## Requirements

- Meta Quest 3 (other Quest models are untested; the two full-resolution layers use about 1.1 GB of memory).
- Developer Mode enabled on the headset, to install an APK.
- VR180 photos as **side-by-side equirectangular JPEGs**. GPano (Google Photo Sphere) metadata is read if present; without it, a JPEG is assumed to be 180° × 180° side-by-side. Developed with QooCam 3 Ultra stills (13,888 × 6,944).
- Or **unprocessed QooCam 3 Ultra JPEGs** (two fisheye circles side by side) from a camera modified for VR180, with its calibration file (see below).

## Installing the APK

1. Download `VR180Viewer.apk` from the [Releases](../../releases) page. Its SHA-256 checksum is listed there so you can check the download.
2. Install it with [SideQuest](https://sidequestvr.com/), or with adb:
   ```
   adb install VR180Viewer.apk
   ```
3. Grant **All files access**, so the viewer can read your photos. The simplest way is with adb:
   ```
   adb shell appops set --uid com.grahamwell.vr180viewer MANAGE_EXTERNAL_STORAGE allow
   ```
   If no photos are found, the picker opens with an "Open settings" button and these instructions; the adb route is more reliable.
4. In the headset, the app is listed under **Unknown Sources** in your app library.

## Where to put your photos

Copy them into the Quest's **Pictures** folder, in any subfolder you like (connect the Quest to your PC by USB and open *Quest 3 › Internal shared storage › Pictures*). This is the same layout ImmerGallery uses, so both apps can share the same photos.

Flat exports are saved to *Pictures/VR180 Flat*.

## Importing from a QooCam 3 Ultra (modified for VR180)

1. **Calibration, once per camera.** Calibrate the camera in QooCam Studio on your PC as usual. Studio saves the result as
   `%APPDATA%\QooCamStudio\calibration\<serial>\<serial>_refined.xml`.
   Copy that file into **Pictures/QooCam Calibration** on the Quest (the folder appears after the viewer's first run). Several cameras can share the folder: each photo is matched to its camera by the serial number in its EXIF data. On a PC the viewer reads Studio's folder directly.
   Without the file, the viewer uses a built-in calibration from another camera and shows a "Camera not calibrated" notice; the two eyes may then not line up.
2. **Import.** Plug the camera into the Quest's USB-C port and switch it on. Press **B** for the picker; at the top level choose **Import from camera**, allow USB access when Android asks, then import the newest 5 (as a test) or all new photos. They go into *Pictures/QooCam Import*. JPEGs only: DNG files stay on the camera.
3. **View.** Choose the folder in the picker as usual. Each photo is converted from fisheye as it loads; the files themselves aren't changed.

The conversion matches QooCam Studio's geometry to within a pixel, including levelling the horizon from the camera's gravity sensor (the **Level Fisheye Photos** setting on the Viewer object turns this off). Studio's colour and tone processing, and its lens-shading correction, aren't reproduced: Studio usually works from the DNG.

If the camera isn't detected, unplug and replug it, or leave the app and come back: the connection can be hit and miss.

## Controls

| Control | Action |
|---|---|
| Either thumbstick left / right | Previous / next photo |
| Right **B** | Open or close the folder picker (also has the Exit button) |
| Right **A** | Open or close the adjustments panel |
| Right trigger | Mark or unmark a favourite (select and drag in the picker and panel) |
| Hold right thumbstick click | Recentre the view |
| Left trigger | Flat export: enter framing mode, then press again to save |
| Left thumbstick (framing) | Zoom the framing guide |
| Left grip (framing) | Cancel the flat export |

In the Unity Editor: arrow keys to step through photos, space to mark a favourite, F for flat export.

Per-photo adjustments and favourites are stored inside the app's own data folder, so uninstalling the app erases them.

> **Careful:** "Tidy" in the picker permanently deletes the non-favourite photos in that folder (after a confirmation page).

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

The code is all in `Assets/VR180`.

| File | Purpose |
|---|---|
| `VR180Viewer.cs` | Main controller: lists and decodes photos, reads GPano metadata, extracts each eye, drives the two compositor layers, transitions and input. |
| `VR180StereoEquirectHandler.cs` | Replaces Unity's OpenXR equirect layer handler on the Quest (when `XR_KHR_composition_layer_equirect2` is available). |
| `VR180StereoEquirect.shader` | The head-following sphere: preview, dead-zone fill and transitions. |
| `VR180EyeExtract.shader` | Copies one eye at one source pixel per output pixel, with a feathered edge and baked-in adjustments. |
| `VR180PhotoPicker.cs` | In-headset folder picker, favourites and tidy. |
| `VR180AdjustPanel.cs`, `VR180Adjustments.cs` | Adjustments panel and per-photo settings. |
| `VR180Favourites.cs`, `VR180FavouriteBadge.cs` | Favourites list, sounds and star. |
| `VR180FlatExport.cs`, `Resources/VR180FlatExport.shader` | Flat 2D JPEG export. |
| `VR180LoadingIndicator.cs` | Loading dots while a photo decodes. |
| `Editor/VR180AndroidManifest.cs` | Adds the All files access permission to the Android manifest. |
| `VR180CameraImport.cs`, `Assets/Plugins/Android/CameraImport.java` | Import from a camera over USB (Android MTP), with the picker's import pages. |
| `VR180Fisheye.cs`, `VR180Fisheye.hlsl`, `Resources/VR180FisheyeToEquirect.shader` | Raw dual-fisheye conversion: calibration files, levelling, the lens model. `VR180EyeExtract.shader` uses it to make each eye straight from the fisheye image. |
| `VR180Notice.cs` | Short notices in the headset, such as a missing calibration file. |

Notes for developers, found the hard way:

- **Two layers, not one.** OpenXR 1.16.1's equirect handler sends the left texture to both eyes (per-eye targeting arrives in 1.18), so the viewer uses one layer per eye with its own handler.
- **Lower vertical angle.** Composition Layers 2.6 measures it from the equator, but OpenXR 1.16.1 negates it, giving a zero-height band. The custom handler passes angles through unchanged.
- **Write-once swapchains.** Unity's handler copies the source into the swapchain every frame; this one writes each swapchain image once and then frees the eye textures (about 380 MB saved).
- **Quest Link** has no `equirect2`, so on PC the layer is off and the photo is shown on the sphere at full resolution instead.

If you move to OpenXR 1.18 or later, these workarounds may become unnecessary or wrong. Try it in a copy of the project.

## Known limitations

- Photos without GPano metadata are assumed to be 180° × 180° side-by-side, so flat photos or 360° images display wrongly.
- Raw QooCam conversion supports the QooCam 3 Ultra's 13,888 × 6,944 stills (other sizes are scaled from the calibration, untested). Lens shading and Studio's colour processing aren't applied.
- Top-bottom stereo shows in mono.
- No video support (yet).
- Flat export over Quest Link is unreliable.
- If the headset sleeps, the photo may stay hidden until you change photo.

## Contributing

Bug reports, test results from other cameras and pull requests are all welcome. Please open an [issue](../../issues) describing your headset, camera and what happened.

## Licence

The project's own code is released under the [MIT Licence](LICENSE). Unity template, sample and package assets included in the project remain under their own licences.
