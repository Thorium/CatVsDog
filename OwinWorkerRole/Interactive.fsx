#r "System.Configuration.dll"

// Dynamic
#r "Microsoft.CSharp.dll"
#load "Dynamic.fs"

#load "TaskHelper.fs"

#load "Tools.fs"

// Azure
#r "../packages/Fog.0.1.4.1/Lib/Net40/Fog.dll"
#r "System.Data.Services.Client.dll"
#r "Microsoft.WindowsAzure.StorageClient.dll"

#load "TableStorage.fs"

#load "AgentState.fs"
open OwinWorkerRole
#load "TaskCancellationAgent.fs"
#load "BusinessLogicActions.fs"
#load "BusinessLogicValidation.fs"

//OWIN part 1
#r "../packages/Microsoft.Owin.2.1.0/lib/net45/Microsoft.Owin.dll"
#r "../packages/Microsoft.AspNet.WebApi.Owin.5.1.0/lib/net45/System.Web.Http.Owin.dll"

#load "HashCheck.fs"

#load "RestApi.fs"

//SignalR
#r "../packages/Newtonsoft.Json.5.0.8/lib/net45/Newtonsoft.Json.dll"
#r "../packages/Microsoft.AspNet.SignalR.Core.2.0.2/lib/net45/Microsoft.AspNet.SignalR.Core.dll"

#load "SignalRHub.fs"

#r "../packages/Microsoft.AspNet.Identity.Owin.1.0.0/lib/net45/Microsoft.AspNet.Identity.Owin.dll"

// Http extensions
#r "System.Net.Http.dll"
#r "System.Net.Http.Formatting.dll"
#r "System.Web.Http.dll"

#load "System.Net.Http.fs"
#load "System.Web.Http.fs"

#load "WebApi.fs"

#r "System.IO.Compression.dll"
#r "System.IO.Compression.FileSystem.dll"

// OWIN part 2
#r "../packages/Owin.1.0/lib/net40/Owin.dll"
#r "../packages/Microsoft.Owin.Security.Cookies.2.1.0/lib/net45/Microsoft.Owin.Security.Cookies.dll"
#r "../packages/Microsoft.Owin.Security.Facebook.2.1.0/lib/net45/Microsoft.Owin.Security.Facebook.dll"
#r "../packages/Microsoft.Owin.Security.MicrosoftAccount.2.1.0/lib/net45/Microsoft.Owin.Security.MicrosoftAccount.dll"
#r "../packages/Microsoft.Owin.Security.Google.2.1.0/lib/net45/Microsoft.Owin.Security.Google.dll"
#r "../packages/Microsoft.Owin.Hosting.2.1.0/lib/net45/Microsoft.Owin.Hosting.dll"
#r "../packages/Microsoft.Owin.Diagnostics.2.1.0/lib/net40/Microsoft.Owin.Diagnostics.dll"
#r "../packages/Microsoft.Owin.Security.2.1.0/lib/net45/Microsoft.Owin.Security.dll"
#r "../packages/Microsoft.Owin.Host.HttpListener.2.1.0/lib/net45/Microsoft.Owin.Host.HttpListener.dll"
//#r "../packages/Microsoft.Owin.Host.SystemWeb.2.1.0/lib/net45/Microsoft.Owin.Host.SystemWeb.dll"


#load "Startup.fs"

#r "Microsoft.WindowsAzure.Diagnostics.dll"
#r "Microsoft.WindowsAzure.ServiceRuntime.dll"
#load "WorkerRole.fs"

////Let's start Owin-server on F#-interactive!
OwinWorkerRole.StartConsoleApp.main ()

//main ()
//
////----------------------------------------------------------------------------------------------------------------------
////This sends a message to all clients
//sendAll "hello"
//
////This would send "ping!" to all clients every 5 seconds
//SignalRCommunication()
