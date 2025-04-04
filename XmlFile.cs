using System.Text;
using System.Xml;

namespace UnityModPackager;

public class XmlFile
{
    public string Base { get; private set; }

    private XmlDocument _xmlDoc;
    private string? _fileName;

    public XmlFile(string path) : this(File.ReadAllBytes(path))
    {
        _fileName = path;
    }

    public XmlFile(byte[] fileData)
    {
        _xmlDoc = new XmlDocument();
        Base = System.Text.Encoding.UTF8.GetString(fileData);
        var byteOrderMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());
        if (Base.StartsWith(byteOrderMarkUtf8))
            Base = Base.Remove(0, byteOrderMarkUtf8.Length);
        
        _xmlDoc.LoadXml(Base);
    }
    
    public bool ContainsChild(string text)
    {
        text = RemoveWhitespace(text);
        return _xmlDoc.DocumentElement!.ChildNodes.Cast<XmlLinkedNode>().Any(child => RemoveWhitespace(child.OuterXml) == text);
    }

    public void RemoveChild(string text)
    {
        text = RemoveWhitespace(text);
        _xmlDoc.DocumentElement!.ChildNodes.Cast<XmlLinkedNode>().Where(c => RemoveWhitespace(c.OuterXml) == text)
            .ToList()
            .ForEach(c => _xmlDoc.DocumentElement.RemoveChild(c));
    }
    
    public void InsertText(int index, string text)
        => Base = Base.Insert(index, text);
    
    public void SaveBase()
    {
        if (_fileName is null)
            throw new InvalidOperationException("File name is not set.");
        
        File.WriteAllText(_fileName, Base);
    }
    
    public void SaveDocument()
    {
        if (_fileName is null)
            throw new InvalidOperationException("File name is not set.");

        using var writer = new XmlTextWriter(_fileName, Encoding.UTF8);
        writer.Formatting = Formatting.Indented;
        writer.Indentation = 4;
        _xmlDoc.Save(writer);
    }
    
    private static string RemoveWhitespace(string str)
        => System.Text.RegularExpressions.Regex.Replace(str, @"\s+", "");

    public void AppendChild(string text)
    {
        var node = new XmlDocument();
        node.LoadXml(text);
        _xmlDoc.DocumentElement?.AppendChild(_xmlDoc.ImportNode(node.DocumentElement!, true));
    }

    public void RemoveAttribute(string text)
    {
        _xmlDoc.DocumentElement?.RemoveAttribute(text);
    }

    public void RemoveItem(string target, params (string attribute, string value)[] attributes)
    {
        var nodes = _xmlDoc.GetElementsByTagName(target).Cast<XmlElement>().ToArray();
        foreach (var node in nodes)
        {
            if (attributes.All(attr => node.GetAttribute(attr.attribute) == attr.value))
            {
                node.ParentNode?.RemoveChild(node);
            }
        }
    }
    
    public void AddAttribute(string tag, string name, object value)
    {
        var nodes = _xmlDoc.GetElementsByTagName(tag).Cast<XmlElement>().ToArray();
        foreach (var node in nodes)
            node.SetAttribute(name, value.ToString()?.ToLowerInvariant());
    }
    
    public void AddTagToRoot(string tag, params (string Name, string Value)[] attributes)
    {
        var node = _xmlDoc.CreateElement(tag);
        foreach (var attribute in attributes)
        {
            var attr = _xmlDoc.CreateAttribute(attribute.Name);
            attr.Value = attribute.Value;
            node.Attributes.Append(attr);
        }
        
        // make sure the tag (or a copy of) is not already in the root
        if (_xmlDoc.DocumentElement?.ChildNodes.Cast<XmlLinkedNode>().Any(c => c.OuterXml == node.OuterXml) == true)
            return;
        
        _xmlDoc.DocumentElement?.AppendChild(node);
    }

    public void AddTag(string tag, string name, object value)
    {
        var nodes = _xmlDoc.GetElementsByTagName(tag).Cast<XmlElement>().ToArray();
        foreach (var node in nodes)
            if (!node.InnerXml.Contains("<" + name + ">"))
                node.InnerXml += $"<{name}>{value.ToString()?.ToLowerInvariant()}</{name}>";
    }

    public void AddTagToFirst(string tag, string name, bool value)
    {
        var node = _xmlDoc.GetElementsByTagName(tag).Cast<XmlElement>().First();
        if (node.InnerXml.Contains("<" + name + ">"))
            return;
        
        var newNode = _xmlDoc.CreateElement(name);
        newNode.InnerText = value.ToString().ToLowerInvariant();
        node.AppendChild(newNode);
    }
}