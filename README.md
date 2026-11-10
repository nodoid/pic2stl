# Pic2Stl

Turn a photo into a printable, multi-colour 3D model, on the device. Point the
app at a picture, tap the thing you want to keep, say which filaments are loaded,
and get an STL for the shape and a 3MF whose colours arrive in a slicer as
separate spools.

Everything runs on the phone: the depth estimation, the meshing and the export.
No photo leaves the device and nothing is sent to a service.

## What it does

- **Pick the object with a tap.** The object is grown from the pixel you touch,
  and its outline is drawn over the photo. Dragging that outline adds or trims
  edges with a brush; the found object can be tuned with a strictness slider.
- **Print in your own colours.** The print colours are the filaments you have,
  named by you and kept between launches, and the photo is mapped onto them
  rather than onto colours found in the picture.
- **Depth from AI.** [Depth Anything V2 Small](https://huggingface.co/onnx-community/depth-anything-v2-small),
  quantised and run locally with ONNX Runtime, turns the photo into a depth
  field. A heuristic estimator is bundled as a fallback.
- **A solid, not a relief.** The depth field becomes a closed solid: a front
  relief, a guessed mirrored back, and a rim that closes the two together.
- **Exports that behave in a slicer.** The STL is one watertight solid for the
  shape. The 3MF is built colour by colour, with each colour closed as its own
  solid and given its own spool in `Metadata/model_settings.config`, which is
  what slicers such as ElegooSlicer and Orca actually read. No print settings are
  written into the file, so the profile you chose is left alone.

## How it works

```
photo ──► ImageData ──► depth (AI or heuristic) ──► smoothing
             │                                        │
             ├──► object selection ──► depth trimmed to the object
             │                                        │
             └──► print palette ──► material map ─────┤
                                                      ▼
                                        SolidMesher ──► Mesh
                                                      ├──► STL (one solid)
                                                      └──► 3MF (sealed per colour)
```

- `src/Pic2Stl.Core` — imaging (PNG decode/encode, object selection), depth
  (ONNX and heuristic estimators, model download), meshing, palette/mapping,
  export, geometry and gesture maths. No UI, no platform code.
- `src/Pic2Stl` — the .NET MAUI app: the editor, the viewer, the colour picker
  and the platform photo decoders.
- `tests/Pic2Stl.Tests` — NUnit suite for the Core library: PNG round trips,
  selection, palette mapping, mesh closure, exporters and the pipeline.

## Requirements

- .NET 10 SDK with the MAUI workload
- Xcode 27.x for iOS; the Android SDK (API 36) for Android
- iOS 18 or Android 12L (API 32) and newer

The AI weights are not in the repository. On first run the app downloads the
~27 MB quantised model from Hugging Face into its own data directory.

## Build and run

```bash
dotnet build src/Pic2Stl/Pic2Stl.csproj -f net10.0-ios27.1       # iOS
dotnet build src/Pic2Stl/Pic2Stl.csproj -f net10.0-android36.0   # Android

dotnet test tests/Pic2Stl.Tests/Pic2Stl.Tests.csproj
```

Deploying to a physical iPhone needs your own signing identity in
`src/Pic2Stl/Pic2Stl.csproj` (`CodesignKey`, `CodesignProvision`,
`CodesignTeamId`) and a bundle id of your own in place of the current one.

Debug builds can run the whole pipeline headless on the device, without the UI,
by launching with `P2S_SELFTEST=1`; it exercises the native decoder, the AI
runtime, the selection and both exporters, and writes an STL and a 3MF to the
app's cache directory.

## Licence

[The DILLIGAF licence](LICENSE). Do what you like with it: copy, change, sell,
port it, no attribution, no warranty.

Author: Paul F. Johnson.
