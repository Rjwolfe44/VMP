Please, BEFORE asking any question check the fork documentation.

Installation:

---------------------

Client side:
- Copy the contents of VladMultiplayer-client.zip to the KSP folder. After extraction, KSP/GameData should contain both "VladMultiplayer" and "000_Harmony" folders.

DO NOT put the standalone server in your GameData folder!!!

Server side:
- Copy the contents of VladMultiplayer-server.zip to any location of your choice EXCEPT the KSP folder. Put it preferably on C:/, your Desktop, or a dedicated server directory.
- Run Server.exe on Windows, or run "dotnet Server.dll" from inside the VMPServer folder when using the portable .NET runtime path.
- The default game port is UDP 8800. Change it in Config/ConnectionSettings.xml if needed.
- Public server-browser listing requires a VMP-compatible master server in MasterServersList/MasterServersList.txt and RegisterWithMasterServer=true in Config/MasterServerSettings.xml.

--------------------

Remember: Multiplayer and heavily modded installs can interact badly. Test VMP with the smallest mod set possible first.
