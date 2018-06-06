// (C) 2018 Michal Ratajsky <michal.ratajsky@gmail.com>
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json;
using WebSocketSharp;
/*
using UnityEngine;
*/

public class SessionObjectSelectionChangedArgs : EventArgs
{
    public SessionObject Obj { get; }
    public bool IsSelected { get; }

    public SessionObjectSelectionChangedArgs(SessionObject obj, bool isSelected)
    {
        Obj = obj;
        IsSelected = isSelected;
    }
}

public class Session
{
    // TODO: for now the session name is hardcoded, but the server is able
    // to manage multiple sessions
    public static string SessionName = "default";

    public enum ConnectResult {
        Success,                // Connection succeeded
        Error,                  // WebSockets connction failed
        SynchronizationError,   // WebSockets succeeded, but HTTP synchronization failed
        AlreadyConnected
    }
    public enum DisconnectReason {
        Closing,                // Disconnected by calling Close()
        ClosedByServer          // Disconnected by the remote server
    }

    ////////////////// Public properties

    // Events
    public event EventHandler<ConnectResult> OnConnectDone;
    // Disconnected from the server
    public event EventHandler<DisconnectReason> OnDisconnected;
    // Object added by another client
    public event EventHandler<SessionObject> OnObjectAdded;
    // Object added by another client, but file not yet downloaded
    // This event will be followed by OnObjectAdded
    public event EventHandler<SessionObject> OnObjectAddedMetaDataOnly;
    // Object moved by another client
    public event EventHandler<SessionObject> OnObjectMoved;
    // Object removed by another client
    public event EventHandler<SessionObject> OnObjectRemoved;
    // Object selected or deselected by another client
    public event EventHandler<SessionObjectSelectionChangedArgs> OnObjectSelectionChanged;

    public bool Connected { get; private set; }
    public SynchronizationContext Context { get; set; }

    public string HttpHost = "http://localhost:8080";
    public string WSHost = "ws://localhost:8089";
    // public string HttpHost = "http://10.11.96.136:8080";
    // public string WSHost = "ws://10.11.96.136:8089";

    //////////////////

    private bool Connecting;
    private int LastSeq;

    // HTTP stuff
    private readonly HttpClient client = new HttpClient();

    // WebSockets stuff
    private WebSocket wsClient;

    private Dictionary<Guid, SessionObject> objectDict =
        new Dictionary<Guid, SessionObject>();

    private static Session instance;
    public static Session Instance
    {
        get {
            if (instance == null)
                instance = new Session();
            return instance;
        }
    }

    private Session()
    {
        wsClient = new WebSocket(WSHost);
        wsClient.OnOpen += OnWsOpen;
        wsClient.OnClose += OnWsClose;
        wsClient.OnMessage += OnWsMessage;
        client.BaseAddress = new Uri(HttpHost);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        
        timer.Elapsed += ProcessFileDownloadTimer;
        timer.Start();
    }

    private async void OnWsOpen(object sender, EventArgs eventArgs)
    {
        if (!Connecting) {
            Log("Unexpected WS open");
            return;
        }
        var handler = OnConnectDone;
        // Download all meta-data
        Log("Retriving items for the first time...");
        var result = await client.GetAsync($"item/all/{SessionName}");
        if (result.IsSuccessStatusCode) {
            try {
                var s = await result.Content.ReadAsStringAsync();
                // The JSON format is {data: [ item1, item2, ... ]}
                var data = JsonConvert.DeserializeObject<ReceivedData>(s);
                if (data.data.Count == 0)
                    Log("The session is empty");
                foreach (var item in data.data) {
                    try {
                        Log($"Adding session item {item.Uid}");
                        await ProcessNewItem(item);
                    } catch (Exception e) {
                        Log(e.ToString());
                        if (item.Uid != null)
                            Log($"Skipping invalid item {item.Uid}");
                        else
                            Log("Skipping invalid item");
                    }
                }
            } catch (Exception e) {
                Connecting = false;
                Log(e.ToString());
                WsClose();
                if (handler != null)
                    handler(this, ConnectResult.SynchronizationError);
            }
            Connecting = false;
            Connected = true;
            if (handler != null)
                handler(this, ConnectResult.Success);
        } else {
            Connecting = false;
            Log($"Could not download items, result code = {result.StatusCode.ToString()}");
            WsClose();
            if (handler != null)
                handler(this, ConnectResult.SynchronizationError);
        }
    }

    private void OnWsClose(object sender, CloseEventArgs e)
    {
        Log($"WS connection closed, code = {e.code}, reason = {e.reason}")
    }

    #pragma warning disable 0649
    private class ReceivedObject {
        // Basic data
        public string Uid;
        public string Session;
        public string ObjectType;
        public List<float> Position;
        public List<float> Scale;
        public List<float> Rotation;
        // File object
        public string FileName;
        // Url object
        public string Url;
        // Text object
        public string Text;
    }
    private class ReceivedData {
        public List<ReceivedObject> data;
    }
    #pragma warning restore 0649

    // Connect to the server
    public bool Connect(bool maintainSession = false)
    {
        var handler = OnConnectDone;
        if (Connected || Connecting) {
            if (handler != null)
                handler(this, ConnectResult.AlreadyConnected);
            return false;
        }
        Connecting = true;
        try {
            Log($"Connecting to WS host {WSHost}...");
            wsClient.ConnectAsync();
            return true;
        } catch (Exception e) {
            Connecting = false;
            Log(e.ToString());
            if (handler != null)
                handler(this, ConnectResult.Error);
            return false;
        }
    }

    // Close connection to the server
    public void Close()
    {
        if (!Connected)
            return;
        Log("Closing connection...");
        Connected = false;
        WsClose();
        var handler = OnDisconnected;
        if (handler != null)
            handler(this, DisconnectReason.Closing);
    }

    // Add an object to the shared session
    // This method should be called after the object is added to the scene
    // to notify other connected clients
    public async Task<bool> AddObject(SessionObject obj, int maxTries = 10)
    {
        Log($"Adding object {obj.Uid}");
        objectDict.Add(obj.Uid, obj);
        try {
            int tries = 0;
            while (true) {
                tries++;
                try {
                    var r = await client.PostAsync("item/add", obj.GetHttpContent());
                    if (r.IsSuccessStatusCode)
                        return true;
                    if (tries >= maxTries)
                        break;
                } catch {
                    if (tries >= maxTries)
                        throw;
                }
                // Delay a bit before the next try
                await Task.Delay(500);
            }
            Log($"Failed to add object {obj.Uid}");
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to add object {obj.Uid}");
        }
        return false;
    }

    // Get a local object with the given UID
    public SessionObject GetCachedObject(Guid guid)
    {
        try {
            return objectDict[guid];
        } catch (KeyNotFoundException) {
            return null;
        }
    }

    // Get a list of all local objects
    public List<SessionObject> GetAllCachedObjects()
    {
        return new List<SessionObject>(objectDict.Values);
    }

    private class WSMessage {
        public string GetMessageTextData() {
            return JsonConvert.SerializeObject(this);
        }
    }
    private class WSSelectionChangedMessage : WSMessage {
        public string Event = "ITEM_SELECTION_CHANGED";
        public Guid Uid;
        public bool IsSelected;
        public WSSelectionChangedMessage(Guid uid, bool isSelected) {
            Uid = uid;
            IsSelected = isSelected;
        }
    }

    // Mark object as selected or deselected for other clients
    // This method should be called when selection changes in the scene
    public bool MarkObjectSelected(SessionObject obj, bool isSelected = true)
    {
        Log($"Marking object {obj.Uid} -> {isSelected}");
        // Object selection is not stored here, we just send it out
        var message = new WSSelectionChangedMessage(obj.Uid, isSelected);
        try {
            WsSendMessage(message);
            return true;
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to mark object {obj.Uid}");
            return false;
        }
    }

    private class WSMoveMessage : WSMessage {
        public string Event = "ITEM_MOVED";
        public Guid Uid;
        public double[] Position;
        public double[] Scale;
        public double[] Rotation;
        public WSMoveMessage(Guid uid,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null) {
            Uid = uid;
            if (position != null) {
                var v = (Vector3) position;
                Position = new double[] { v.x, v.y, v.z };
            }
            if (scale != null) {
                var v = (Vector3) scale;
                Scale = new double[] { v.x, v.y, v.z };
            }
            if (rotation != null) {
                var v = (Quaternion) rotation;
                Rotation = new double[] { v.x, v.y, v.z, v.w };
            }
        }
    }

    // Move session object to the given position, change scale, rotation
    // Any of position/scale/rotation may be omitted
    // This method should be called after the object is moved in the scene
    // to notify other connected clients
    public bool MoveObject(SessionObject obj,
                           Vector3? position = null,
                           Vector3? scale = null,
                           Quaternion? rotation = null)
    {
        if (position == null && scale == null && rotation == null)
            return false;
        Log($"Moving object {obj.Uid}");
        // Update the object
        if (position != null)
            obj.Position = (Vector3) position;
        if (scale != null)
            obj.Scale = (Vector3) scale;
        if (rotation != null)
            obj.Rotation = (Quaternion) rotation;
        var message = new WSMoveMessage(obj.Uid, position, scale, rotation);
        try {
            // Broadcast the update
            WsSendMessage(message);
            return true;
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to move object {obj.Uid}");
            return false;
        }
    }

    private class WSRemoveMessage : WSMessage {
        public string Event = "ITEM_REMOVED";
        public Guid Uid;
        public WSRemoveMessage(Guid uid) {
            Uid = uid;
        }
    }

    // Remove session object
    // This method should be called after the object is removed from the scene
    // to notify other connected clients
    public bool RemoveObject(SessionObject obj)
    {
        Log($"Removing object {obj.Uid}");
        objectDict.Remove(obj.Uid);
        var message = new WSRemoveMessage(obj.Uid);
        try {
            WsSendMessage(message);
            return true;
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to remove object {obj.Uid}");
            return false;
        }
    }

    // Remove all session objects
    public async Task<bool> RemoveAllObjects()
    {
        Log($"Removing all objects");
        objectDict.Clear();
        try {
            var r = await client.DeleteAsync($"item/all/{SessionName}",
                CancellationToken.None);
            if (r.IsSuccessStatusCode)
                return true;
            Log($"Failed to remove objects, result code = {r.StatusCode.ToString()}");
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to remove objects");
        }
        return false;
    }

    private void Log(string msg)
    {
        Console.WriteLine(msg);
        // Debug.Log(msg);
    }

    private async Task<bool> ProcessFileDownload(SessionObjectRemoteFile obj)
    {
        try {
            var r = await client.GetAsync($"item/download/{obj.Uid}");
            if (r.IsSuccessStatusCode) {
                obj.FileContent = await r.Content.ReadAsByteArrayAsync();
                Log($"Downloaded file {obj.FileName}, size = {obj.FileContent.Length}");
                return true;
            }
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Failed to download file for object {obj.Uid}");
        }
        return false;
    }

    // Might throw on invalid input
    private async Task<bool> ProcessNewItem(ReceivedObject data)
    {
        // We require that the server always sends all of these parts
        if (data.Position == null ||
            data.Scale == null ||
            data.Rotation == null) {
            Log("Missing Position/Scale/Rotation field");
            return false;
        }
        var uid = new Guid(data.Uid);
        var obj = GetCachedObject(uid);
        if (obj != null) {
            Log($"Object already known: {data.Uid}");
            return false;
        }
        switch (data.ObjectType) {
            case "File":
                obj = new SessionObjectRemoteFile(uid, data.FileName);
                break;
            case "Link":
                obj = new SessionObjectLink(uid, new Uri(data.Url));
                break;
            case "Text":
                obj = new SessionObjectText(uid, data.Text);
                break;
            default:
                Log($"Invalid object type: {data.ObjectType}");
                return false;
        }
        // [ x, y, z ]
        obj.Position = new Vector3(
            data.Position[0],
            data.Position[1],
            data.Position[2]);
        obj.Scale = new Vector3(
            data.Scale[0],
            data.Scale[1],
            data.Scale[2]);
        obj.Rotation = new Quaternion(
            data.Rotation[0],
            data.Rotation[1],
            data.Rotation[2],
            data.Rotation[3]);

        Log($"New object: {obj.Uid}");
        objectDict.Add(obj.Uid, obj);
        if (obj is SessionObjectRemoteFile) {
            Context.Post((o) => {
                OnObjectAddedMetaDataOnly(this, obj);
            }, null);
            Log($"Downloading file for object {obj.Uid}...");
            var downloaded = await ProcessFileDownload(obj as SessionObjectRemoteFile);
            if (!downloaded) {
                Log($"Failed to download file for object {obj.Uid}");
                Context.Post((o) => {
                    OnObjectRemoved(this, obj);
                }, null);
                return false;
            }
            // Fall-through as we need to raise the real event
        }
        Context.Post((o) => {
            OnObjectAdded(this, obj);
        }, null);
        return true;
    }

    #pragma warning disable 0649
    private class WSReceivedObject : ReceivedObject {
        // Additional field that may appear in WS messages
        public string Event;
        public int Seq;
        // Selection message
        public bool IsSelected;
    }
    #pragma warning restore 0649

    // Might throw on invalid input
    private async Task<bool> ProcessWSItemAdded(WSReceivedObject data)
    {
        return await ProcessNewItem(data);
    }

    // Might throw on invalid input
    private void ProcessWSItemMoved(WSReceivedObject data)
    {
        // We require at least one of these fields
        if (data.Position == null &&
            data.Scale == null &&
            data.Rotation == null) {
            Log("Missing Position/Scale/Rotation fields");
            return;
        }
        var obj = GetCachedObject(new Guid(data.Uid));
        if (obj == null) {
            Log("Unknown object: " + data.Uid);
            return;
        }
        // [ x, y, z ]
        if (data.Position != null) {
            // Log("New position: " + data["Position"].ToString());
            obj.Position = new Vector3(
                data.Position[0],
                data.Position[1],
                data.Position[2]);
        }
        if (data.Scale != null) {
            // Log("New scale: " + data["Scale"].ToString());
            obj.Scale = new Vector3(
                data.Scale[0],
                data.Scale[1],
                data.Scale[2]);
        }
        if (data.Rotation != null) {
            // Log("New rotation: " + data["Rotation"].ToString());
            obj.Rotation = new Quaternion(
                data.Rotation[0],
                data.Rotation[1],
                data.Rotation[2],
                data.Rotation[3]);
        }
        Context.Post((o) => {
            OnObjectMoved(this, obj);
        }, null);
    }

    // Might throw on invalid input
    private void ProcessWSItemRemoved(WSReceivedObject data)
    {
        var obj = GetCachedObject(new Guid(data.Uid));
        if (obj == null) {
            Log("Unknown object: " + data.Uid);
            return;
        }
        // Remove the object from local cache and raise an event
        objectDict.Remove(obj.Uid);
        Context.Post((o) => {
            OnObjectRemoved(this, obj);
        }, null);
    }

    // Might throw on invalid input
    private void ProcessWSItemSelectionChanged(WSReceivedObject data)
    {
        var obj = GetCachedObject(new Guid(data.Uid));
        if (obj == null) {
            Log("Unknown object: " + data.Uid);
            return;
        }
        Context.Post((o) => {
            OnObjectSelectionChanged(this, new SessionObjectSelectionChangedArgs(obj, data.IsSelected));
        }, null);
    }

    private void WsClose()
    {
        try {
            var state = wsClient.ReadyState;
            Log($"WS state before closing: {state.ToString()}");
            if (state == WebSocketState.Connecting ||
                state == WebSocketState.Open) {
                Log("Closing WS client");
                wsClient.CloseAsync();
            }
        } catch (Exception e) {
            Log(e.ToString());
        }
    }

    private void OnWsMessage(object source, MessageEventArgs args){
        if (args.IsText) {
            var message = args.Data;
            Log($"WS << {message}");
            var item = JsonConvert.DeserializeObject<WSReceivedObject>(message);
            try {
                Log($"Event: {item.Event}, Seq = {item.Seq}");
                if (LastSeq > 0 && LastSeq+1 != item.Seq)
                    Log($"Seq = {item.Seq}, but LastSeq = {LastSeq}");
                LastSeq = Seq;
                switch (item.Event) {
                    case "ITEM_ADDED":
                        ProcessWSItemAdded(item);
                        break;
                    case "ITEM_MOVED":
                        ProcessWSItemMoved(item);
                        break;
                    case "ITEM_REMOVED":
                        ProcessWSItemRemoved(item);
                        break;
                    case "ITEM_SELECTION_CHANGED":
                        ProcessWSItemSelectionChanged(item);
                        break;
                    default:
                        Log($"Ignoring unknown event {item.Event}");
                        break;
                }
            } catch (Exception e) {
                Log(e.ToString());
                if (item.Uid != null)
                    Log($"Skipping invalid item {item.Uid}");
                else
                    Log("Skipping invalid item");
            }
        }
    }

    private void WsSendMessage(WSMessage message)
    {
        var data = message.GetMessageTextData();
        try {
            wsClient.SendAsync(data, (res) => {
                Log($"WS({res}) >> {message}");
            });
        } catch (Exception e) {
            Log(e.ToString());
            Log($"Disconnected when trying to send to WS, Connected={Connected}, Connecting={Connecting}");
            Connected = false;
            if (!Connecting) {
                WsClose();
                var handler = OnDisconnected;
                if (handler != null)
                    handler(this, DisconnectReason.ClosedByServer);
            }
            throw;
        }
    }
}
