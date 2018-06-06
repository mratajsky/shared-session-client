// (C) 2018 Michal Ratajsky <michal.ratajsky@gmail.com>
using System;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json;
/*
using UnityEngine;
*/

// Provide custom Vector3 and Quaternion to allow compiling outside Unity
public struct Vector3 
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public Vector3(float x, float y, float z) {
        this.x = x;
        this.y = y;
        this.z = z;
    }
}
public struct Quaternion
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public float w { get; set; }
    public Quaternion(float x, float y, float z, float w) {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }
}

public abstract class SessionObject
{
    // Object type
    public enum Type {
        File,
        Link,
        Text
    };
    public string ObjectType { get; protected set; }

    // Object identifier as UUID -- auto-generated
    public Guid Uid { get; protected set; }
    
    // Object coordinates
    public Vector3 Position { get; set; }
    public Vector3 Scale { get; set; }
    public Quaternion Rotation { get; set; }

    // Arbitrary object name -- optional
    public string Name { get; set; }

    public SessionObject(Guid uid,
                         Type type,
                         Vector3? position = null,
                         Vector3? scale = null,
                         Quaternion? rotation = null,
                         string name = "")
    {
        Uid = uid;
        switch (type) {
            case Type.File:
                ObjectType = "File";
                break;
            case Type.Link:
                ObjectType = "Link";
                break;
            case Type.Text:
                ObjectType = "Text";
                break;
        }
        if (position != null)
            Position = (Vector3) position;
        else
            Position = new Vector3(0, 0, 0);
        if (scale != null)
            Scale = (Vector3) scale;
        else
            Scale = new Vector3(1, 1, 1);
        if (rotation != null)
            Rotation = (Quaternion) rotation;
        else
            Rotation = new Quaternion(0, 0, 0, 0);
        Name = name;
    }

    protected MultipartFormDataContent GetInitialContent()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(Uid.ToString()), "Uid");
        content.Add(new StringContent(Session.SessionName), "Session");
        content.Add(new StringContent(ObjectType), "ObjectType");
        var position = new double[] {
            Position.x,
            Position.y,
            Position.z
        };
        content.Add(new StringContent(JsonConvert.SerializeObject(position)), "Position");
        var scale = new double[] {
            Scale.x,
            Scale.y,
            Scale.z
        };
        content.Add(new StringContent(JsonConvert.SerializeObject(scale)), "Scale");
        var rotation = new double[] {
            Rotation.x,
            Rotation.y,
            Rotation.z,
            Rotation.w
        };
        content.Add(new StringContent(JsonConvert.SerializeObject(rotation)), "Rotation");
        return content;
    }

    abstract public HttpContent GetHttpContent();
}

public class SessionObjectFile : SessionObject
{
    public string FileName { get; }
    private FileStream stream;

    public SessionObjectFile(Guid uid,
                             string fileName,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : base(uid, Type.File, position, scale, rotation, name)
    {
        FileName = fileName;
    }
    public SessionObjectFile(string fileName,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : this(Guid.NewGuid(), fileName, position, scale, rotation, name)
    {
    }

    public override HttpContent GetHttpContent()
    {
        var content = GetInitialContent();
        // The FileName also contains a path property, but we only send
        // the name to the server
        content.Add(new StringContent(Path.GetFileName(FileName)), "FileName");
        if (stream == null)
            stream = File.Open(FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        content.Add(new StreamContent(stream), "FileContent");
        return content;
    }
}

public class SessionObjectRemoteFile : SessionObjectFile
{
    public byte[] FileContent { get; set; }

    public SessionObjectRemoteFile(Guid uid,
                                   string fileName,
                                   Vector3? position = null,
                                   Vector3? scale = null,
                                   Quaternion? rotation = null,
                                   string name = "")
        : base(uid, fileName, position, scale, rotation, name)
    {
    }
    public SessionObjectRemoteFile(string fileName,
                                   Vector3? position = null,
                                   Vector3? scale = null,
                                   Quaternion? rotation = null,
                                   string name = "")
        : this(Guid.NewGuid(), fileName, position, scale, rotation, name)
    {
    }

    public override HttpContent GetHttpContent()
    {
        var content = GetInitialContent();
        content.Add(new StringContent(FileName), "FileName");
        content.Add(new ByteArrayContent(FileContent), "FileContent");
        return content;
    }
}

public class SessionObjectLink : SessionObject
{
    public Uri Url { get; }

    public SessionObjectLink(Guid uid,
                             Uri url,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : base(uid, Type.Link, position, scale, rotation, name)
    {
        Url = url;
    }
    public SessionObjectLink(Uri url,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : this(Guid.NewGuid(), url, position, scale, rotation, name)
    {
    }

    public override HttpContent GetHttpContent()
    {
        var content = GetInitialContent();
        content.Add(new StringContent(Url.ToString()), "Url");
        return content;
    }
}

public class SessionObjectText : SessionObject
{
    public string Text { get; }

    public SessionObjectText(Guid uid,
                             string text,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : base(uid, Type.Text, position, scale, rotation, name)
    {
        Text = text;
    }
    public SessionObjectText(string text,
                             Vector3? position = null,
                             Vector3? scale = null,
                             Quaternion? rotation = null,
                             string name = "")
        : this(Guid.NewGuid(), text, position, scale, rotation, name)
    {
    }

    public override HttpContent GetHttpContent()
    {
        var content = GetInitialContent();
        content.Add(new StringContent(Text), "Text");
        return content;
    }
}
