﻿namespace Manager.Shared.Entities;

public class DirectoryItem
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsRoot { get; }
    
    public DirectoryItem(string name, string fullPath, bool isRoot)
    {
        this.Name = name;
        this.FullPath = fullPath;
        this.IsRoot = isRoot;
    }
}