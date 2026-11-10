# Photos in the gallery: info, location, editing

[Deutsch](galerie-fotos.de.md) · English

Status: version 2. Applies to photos in your own files and in gallery folders (external folders added to the gallery).

## Where the information lives

dmOffice writes photo information where other programs (Lightroom, darktable, digiKam, Windows) read it, without exiftool:

- **JPEG:** into the file itself. Only the metadata blocks (EXIF, XMP) are replaced; the image data stays byte for byte the
  same. Location goes into EXIF GPS, title, caption, rating and keywords into XMP.
- **RAW:** never into the file. Changes go into the XMP sidecar next to it: an existing `photo.arw.xmp` or `photo.xmp`,
  otherwise a new `photo.xmp`. Data from other programs in that file (Lightroom develop settings, darktable history,
  hierarchical keywords …) is kept.
- After writing, dmOffice reads the result back and checks it (image data unchanged, capture time and camera as before,
  location as intended). If the check fails, the file is not changed.

For gallery folders the folder must be mounted writable. For RAW photos in a gallery folder, "XMP files" must be switched
on for that folder (*Gallery* → *Gallery folders*); otherwise the gallery would not read what was written.

When reading, a sidecar takes precedence over the information inside the photo, and XMP takes precedence over IPTC.

## Title, caption, rating, keywords

Select one or more photos → *Info* in the ribbon, or *Title, keywords, rating …* in the context menu (app: selection →
menu). With several photos:

- Fields you do not touch stay as they are on each photo. A field that differs shows "Varies".
- Keywords show how many of the photos have them ("Mountains (3/5)"). Added keywords go to all selected photos, removed
  ones are removed from all. Removing also removes them from Lightroom's hierarchical keywords, so they do not come back.

The gallery search finds titles, captions and keywords.

## Location

- **By hand:** select photos → *Atlas* → *Set location for n selected photos* → tap the map. An existing location is
  replaced after a confirmation.
- **From sidecars:** a location written by Lightroom into `photo.xmp` shows up in the atlas and the detail pane.

With photos selected, the atlas shows only those.

## Photo engine

RAW development and editing suggestions are handled by the photo engine. It is included in dmOffice, runs only on your server
and starts with the first job; after ten minutes without a job dmOffice stops it again. You can switch it off under *Administration* →
*Modules*. On another computer: start `photo-engine/` there and give dmOffice `PHOTO_ENGINE_URL` (optionally `PHOTO_ENGINE_TOKEN`). Full
resolution needs 0.3 to 1 GB of memory depending on the photo; a memory limit on the container should allow for that.

## Editing without changing the original

The photo editor stores rotation, crop and sliders as settings in the database. The original file is not changed.
Thumbnails and the viewer show the edited version; tiles and the viewer mark it as "edited" (hold *Original* to compare).
*Export as JPG* creates a copy, *Remove editing* brings back the original. *Improve automatically* also stores only settings.
