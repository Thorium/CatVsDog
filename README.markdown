**Azure Owin F-Sharp:**

This is generic cloud based online collaboration/gaming platform.
But because generic sounds bad, let's specify more:

This is a architecture of users (players) and rooms (games).
Users can join rooms. They can chat with other users in the rooms.
Users can co-operate with other room users, e.g. play games.

Claims based authentication:
Facebook / Google+ (/...) external logins are supported 

See the high level picture: 
![Architecture.jpg][1]

The whole system runs in cloud as Windows Azure Worker role.
Worker role hosts OWIN (Katana) based web server. 
Web server hosts (Hateoas) REST-based WebApi for data communication.
And SignalR (Hub) for web-based Publish/Subscribe -pattern to enable
real-time bi-directional communication between users,
usually based on WebSockets. 
Web server tunnel static HTML-files from Windows Azure Blob Storage.

User data is stored to Windows Azure Table Storage 
(which is a NoSQL document database).

Runtime data uses Actor/Agent -based communication:
Each user has an agent. Each room has an agent.
There is (generic strongly typed) control agent class which can 
create other agents. (e.g. one instance for room-agents, one for user-agents)

Programming language is F-Sharp (F#). It is a multi-paradigm 
(functional-first) programming language mainly for .NET environment.
F# related technologies: Fog is used for Azure communication. 
FSharp.Net.Http and FSharp.Web.Http is used for HTTP communication.

This is developed with 
Visual Studio 2012 (Update 4) / Visual Studio 2013 
and Azure SDK 2.2. References are resolved via NuGet. 

To run:
Build and run from Visual Studio
See the host console from (task tray icon) Azure Compute Emulator.
There is a line something like: "Starting OWIN at http://127.255.0.0:81"
(If compute emulator logging works...)
Copy the address and open it with web browser.
(...or you can deploy this to Windows Azure)
![StartInstructions.jpg][2]


Sample HTML-pages are just HTML5 with jQuery and Knockout.js
They are deployed from the wwwroot.zip -file.

![Screenshot.jpg][3]

[![Build status](https://ci.appveyor.com/api/projects/status/fvrimln2nc6m29pp)](https://ci.appveyor.com/project/Thorium/catvsdog)

   [1]: https://raw.github.com/Thorium/CatVsDog/master/specifications/Architecture.jpg
   [2]: https://raw.github.com/Thorium/CatVsDog/master/specifications/StartInstructions.jpg
   [3]: https://raw.github.com/Thorium/CatVsDog/master/specifications/Screenshot.jpg
   
