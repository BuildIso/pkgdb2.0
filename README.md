# pkgdb2.0
pkgdb2.0 is a successor of pkgdb

# pkgdb2.0: how to request to add a package? 
To add a package, the easiest way is to first fork the repository. You must keep exactly the same repository name in your fork as the original repository (pkgdb2.0), then download pkgdb2.0.exe from the releases, then run it, type auth, enter your username and your token. Please refer to the section "how can I create my token?" Then, type create <vendor> <app> <version> <url> [type] example: create MyAuthor MyGame 2.0.0 https://myauthor.test/mygame/2.0.0/download/mygame.exe exe, then click on the PR link displayed at the end of the terminal and wait for the checks by the bot. If the checks are not passed, a human will come to ask you questions.

# how can I create my token? 
To create your token, go to this link https://github.com/settings/tokens then click on generate a new token (classic). Check the repo box. The token name is not important, you can name it whatever you want.

# Where is my exe after installing MyGame in pkgdb2.0?
Go to the link that the terminal gave you (Installer downloaded to: ...), look for the name you installed, you will see a .bin (MyGame_VERSION.bin), but how to turn the .bin into a .exe is very simple! Change the extension from .bin to .exe
