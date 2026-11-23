# GunSpray [NOT YET RELEASED]
GunSpray is a plugin for Pan4ur's Gunsaw Multiplayer mod. It aims to bring about various enhancements to the Spray / Graffiti system.

The plugin primarily adds / changes 5 things to the default Spray / Graffiti system present.
1) Alphabetical sorting for images.
2) Subfolder support
3) Troubleshooting support for invalid spray images
4) Raised spray image cap from 128 to 512.
5) .webp and (light) .gif file support.

## Alphabetical sorting for images.
Images (or subfolders) will now be sorted Alphabetically, allowing users to get a rough estimation for where newly added Sprays will go in the list.
## Subfolder support.
Folders greated inside of BepInEx/plugins/Graffiti will now appear in the Graffiti menu in-game, allowing greater organization. Folders can also be nested inside of eachother.
## Troubleshooting support.
A persistent reminder is also placed at the top remindign users that for a Spray to show up in-game, it needs to be both under 1 megabyte big and also less than 1280 pixels in either the horizontal or vertical axis. Additionally, if an image does not fulfill those 2 requirements, it will still show up in the menu but warn the user it is not usable, so tweaks may be manually applied to make it compatible. As a final solution, the plugin will also log any issues with sprays inside of the BepInEx log, for extreme edge cases where images do not show up at all in the first place.
## Raised spray image cap.
Simply put, raises the cap of images that can be loaded as sprays from 128 to 512. This is possible thanks to implementing lazy loading and caching into the Graffiti menu. This leads to nearly 0 performance loss even when the cap is reached (In testing, a delay of 0.5 seconds was noticed when opening the spray menu)
## .webp and .gif file support.
.webp and .gif images will now show up in the Spray menu with varying degrees of compatibility; .webp images will support their full feature set, and follow the same rules as .jpg/.jpeg/.png images (1mb max, no bigger than 1280px), transparency remains, and for animated .webp images, only the first frame is fetched and used. As for .gif files, transparency is still retained, and for animated .gif images, the first frame is fetched and used.
