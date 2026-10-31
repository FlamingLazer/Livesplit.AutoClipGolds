# Changelog

## v0.9.6 (prerelease)

- Fix unstable layout checks caused by repeatedly encrypting an unchanged OBS password. LiveSplit can now reuse its normal update path instead of unnecessarily invalidating the layout.
- Add a lightweight settings hash; cache the encrypted password until it changes. Existing saved settings remain compatible.
- Defer gold checks to an on-demand background worker after completed split events, preserving event timestamps and undo/re-split order.
- Cache previous matching comparison times and move log writes away from timer callbacks.
- Keep undo from waiting on worker file operations.
- Add regression coverage for stable layout hashes, password caching, asynchronous event handling, capture/undo, and 1,000 non-gold splits.

The affected LEGO Star Wars: The Complete Saga room-split setup was retested successfully. This remains a prerelease; performance can vary across PCs and layouts.

## v0.9.1 (prerelease)

- Preserve LiveSplit segment times, including offsets, in gold filenames.
- Continue removing offset tags from the split-name portion of filenames.
- Keep video boundaries based on actual elapsed time, plus padding.
