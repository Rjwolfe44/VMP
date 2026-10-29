Please, BEFORE asking any question check the fork documentation.

Installation:

---------------------

Client side:
- Copy the contents of VladMultiplayer-client.zip to the KSP folder. After extraction, KSP/GameData should contain both "VladMultiplayer" and "000_Harmony" folders.

DO NOT put the standalone server in your GameData folder!!!

Server side:
- Copy the contents of VladMultiplayer-server.zip to any location of your choice EXCEPT the KSP folder. Put it preferably on C:/, your Desktop, or a dedicated server directory.
- Run Server.exe on Windows, or run "dotnet Server.dll" from inside the VMPServer folder when using the portable .NET runtime path.
- All server settings are in Config/vmp-server.json (created on the first start; older XML settings files are imported once). The default game port is UDP 8800: change it in the "connection" section while the server is stopped.
- Public server-browser listing requires a VMP-compatible master server in MasterServersList/MasterServersList.txt and RegisterWithMasterServer=true in the "masterServer" section of Config/vmp-server.json.

--------------------

Remember: Multiplayer and heavily modded installs can interact badly. Test VMP with the smallest mod set possible first.
