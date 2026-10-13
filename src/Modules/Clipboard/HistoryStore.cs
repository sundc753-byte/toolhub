using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ClipboardApp;

/// <summary>Clipboard history, newest first, persisted under %AppData%\ClipboardApp.</summary>
public sealed class HistoryStore
{
    private const int MaxItems = 100;

    private readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardApp");
    private readonly string _imageDir;
    private readonly string _file;

    public ObservableCollection<ClipItem> Items { get; } = new();

    public HistoryStore()
    {
        _imageDir = Path.Combine(_dir, "images");
        _file = Path.Combine(_dir, "history.json");
        Directory.CreateDirectory(_imageDir);
        Load();
    }

    public string DataDir => _dir;

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(_file)) ?? new();
            foreach (var item in list)
            {
                if (item.IsImage)
                {
                    item.ImagePath = Path.Combine(_imageDir, item.ImageFile ?? "");
                    if (!File.Exists(item.ImagePath)) continue;
                }
                Items.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt history is not worth crashing over; start fresh.
        }
    }

    public void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.ToList()));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException) { }
    }

    /// <summary>Adds text, or moves an identical entry to the top.</summary>
    public (ClipItem Item, bool IsNew) AddText(string text)
    {
        var hash = "T:" + Sha(Encoding.UTF8.GetBytes(text));
        return AddOrBump(hash, () => new ClipItem { Hash = hash, Text = text });
    }

    /// <summary>Adds an image (saved as PNG), or moves an identical entry to the top.</summary>
    public (ClipItem Item, bool IsNew) AddImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        var hash = "I:" + Sha(bytes);

        return AddOrBump(hash, () =>
        {
            var name = hash[2..18] + ".png";
            var path = Path.Combine(_imageDir, name);
            File.WriteAllBytes(path, bytes);
            return new ClipItem
            {
                Hash = hash,
                IsImage = true,
                ImageFile = name,
                ImagePath = path,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
            };
        });
    }

    private (ClipItem, bool) AddOrBump(string hash, Func<ClipItem> create)
    {
        var existing = Items.FirstOrDefault(i => i.Hash == hash);
        if (existing is not null)
        {
            existing.Time = DateTime.Now;
            MoveToTop(existing);
            return (existing, false);
        }

        var item = create();
        Items.Insert(0, item);
        while (Items.Count > MaxItems)
            Remove(Items[^1], save: false);
        Save();
        return (item, true);
    }

    /// <summary>Marks an item as just used by moving it to the top of the list.</summary>
    public void MoveToTop(ClipItem item)
    {
        var idx = Items.IndexOf(item);
        if (idx > 0) Items.Move(idx, 0);
        Save();
    }

    public void Remove(ClipItem item, bool save = true)
    {
        Items.Remove(item);
        DeleteImage(item);
        if (save) Save();
    }

    public void Clear()
    {
        foreach (var i in Items) DeleteImage(i);
        Items.Clear();
        Save();
    }

    private static void DeleteImage(ClipItem item)
    {
        try
        {
            if (item.IsImage && item.ImagePath is not null) File.Delete(item.ImagePath);
        }
        catch (IOException) { }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
