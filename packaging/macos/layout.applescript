on run argv
    set volumePath to item 1 of argv
    tell application "Finder"
        set diskFolder to POSIX file volumePath as alias
        open diskFolder
        set diskWindow to container window of diskFolder
        set current view of diskWindow to icon view
        set toolbar visible of diskWindow to false
        set statusbar visible of diskWindow to false
        set bounds of diskWindow to {140, 100, 1040, 720}
        set options to icon view options of diskWindow
        set arrangement of options to not arranged
        set icon size of options to 78
        set text size of options to 14
        set background picture of options to file ".background:background.png" of diskFolder
        set position of item "v2rayN.app" of diskFolder to {200, 100}
        set position of item "Applications" of diskFolder to {650, 100}
        set position of item "安装说明.txt" of diskFolder to {795, 100}
        update diskFolder without registering applications
        delay 2
        close diskWindow
    end tell
end run
