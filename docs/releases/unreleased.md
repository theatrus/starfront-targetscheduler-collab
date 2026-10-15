# Unreleased

## Starfront API compatibility

- Retain Starfront's colour-camera and Bortle limits when reading nightly work,
  and hold work for OSC bands explicitly disabled by the project.
- Normalize Starfront OSC and multiband names without turning them into mono
  channels. Keep the physical filter for acquisition and save its delivered
  bands with each nightly plan for delayed contribution reports.
- Hold conflicting same-night band mappings and camera-type mismatches for
  review. Custom channel commissioning and Starfront file uploads remain
  separate, unsupported extensions.
