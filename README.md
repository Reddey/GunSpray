<img width="960" height="263" alt="logo" src="https://github.com/user-attachments/assets/99adb42b-6b1b-420d-9989-9c78c3a48139" />
GunSpray is a plugin for Pan4ur's Gunsaw Multiplayer mod. It aims to bring about various enhancements to the Spray / Graffiti system.

https://github.com/user-attachments/assets/57148fca-e84c-4ba1-ae90-a8c00da08505

# The plugin primarily implements 5 changes:
1) Alphabetical sorting for sprays
2) Subfolder support
3) Troubleshooting support for invalid spray images
4) Raised spray image cap from 128 to 512
5) .webp and (light) .gif file support

## 1) Alphabetical sorting for images.
Images (and subfolders) will now be sorted Alphabetically, giving users a consistent way of getting a rough idea where new sprays will be placed.
<img width="1158" height="610" alt="Alphabetical Sorting" src="https://github.com/user-attachments/assets/48d78ff2-1111-4452-ba71-6ad63101028c" />
## 2) Subfolder support.
Folders created inside of BepInEx/plugins/Graffiti will now appear in the Graffiti menu in-game, allowing for greater organization. Folders can also be nested inside of each other, to offer even more organization potential.
<img width="1276" height="1491" alt="Folders" src="https://github.com/user-attachments/assets/fbfbb369-f154-40df-b0f6-ad50e9cf43f8" />
## 3) Troubleshooting support.
A persistent reminder is also placed at the top reminding users that for a Spray to show up in-game, it needs to be both under 1 megabyte big and also less than 1280 pixels across in either the horizontal or vertical axis. Additionally, if an image does not fulfill those 2 requirements, it will still show up in the menu but warn the user it is not usable, so tweaks may be manually applied to make it compatible. As a final solution, the plugin will also log any issues with sprays inside of the BepInEx log, for extreme edge cases where images do not show up at all in the Graffiti menu in the first place.
<img width="1416" height="942" alt="Warnings" src="https://github.com/user-attachments/assets/e827ef79-bf71-4152-b00d-2928bf1c9590" />
## 4) Raised spray image cap.
The mod also additionally raises the spray image cap from 128 to 512. This is possible thanks to implementing lazy loading and caching into the Graffiti menu. This leads to nearly 0 performance loss even when the cap is reached (In testing, a delay of 0.5 seconds was noticed when opening the spray menu)
<img width="1352" height="900" alt="Raised Cap" src="https://github.com/user-attachments/assets/a85627c1-8293-4501-b60a-f91c747de71d" />
## 5) .webp and (light) .gif file support.
.webp and .gif images will now show up in the Spray menu with varying degrees of compatibility; .webp images will support their full feature set, and follow the same rules as .jpg/.jpeg/.png images (1mb max, no bigger than 1280px), transparency remains, and for animated .webp images, only the first frame is fetched and used. As for .gif files, transparency is still retained, and for animated .gif images, the first frame is fetched and used.
<img width="1285" height="1602" alt=".webp and .gif Support" src="https://github.com/user-attachments/assets/ef21f359-a228-444b-bec2-62a687a76cc0" />

# Other information
- For the .webp integration to work, GunSpray will create a temporary folder with a converter in %Temp%. If you wish to uninstall the plugin, make sure to delete the "GunSpray" folder located there as well.
- GunSpray utilizes the GNU GPLv3 license, and part of it is possible thanks to StbImageSharp, Googles libwebp and Imazen WebP Native Runtime.
