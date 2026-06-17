# Publishing Audio Status

This repo is prepared for publishing Audio Status as a Microsoft PowerToys Command Palette extension.

## Required external setup

1. Use the public project repository at `https://github.com/hansek/AudioStatusExtension`.
2. Use Microsoft Store product `9NRHJDKHMOQS`.
3. Keep `Package.appxmanifest` identity aligned with Partner Center:
   - `Package/Identity/Name`: `JanTezner.AudioStatusforCommandPalette`
   - `Package/Identity/Publisher`: `CN=3EAEAC55-63BF-4FAD-B765-EC11C364E23F`
   - `Package/Properties/PublisherDisplayName`: `Jan Tezner`
4. Build `x64` and `arm64` packages for release `0.1.0`.
5. Upload the packages to the Microsoft Store submission.
6. Once the product is live, update the Command Palette gallery install source to Microsoft Store product `9NRHJDKHMOQS`.
7. Submit `distribution/cmdpal-gallery/extensions/jan-tezner/audio-status/` to `microsoft/CmdPal-Extensions` under `extensions/jan-tezner/audio-status/`.

## Command Palette gallery payload

The gallery submission is prepared here:

```text
distribution/cmdpal-gallery/extensions/jan-tezner/audio-status/
  extension.json
  icon.png
```

The gallery requires an already-published install source. Use Microsoft Store product `9NRHJDKHMOQS` once the app is live.

## Winget payload

The winget submission template is prepared here as a later alternative to Microsoft Store:

```text
distribution/winget/manifests/j/JanTezner/AudioStatus/0.1.0/
```

Before submitting, replace every `REPLACE_WITH_*` placeholder with the final hashes.
