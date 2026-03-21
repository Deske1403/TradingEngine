using System;
using System.IO;
using System.Text.Json;
var path = @"c:\Users\Lenovo\source\repos\Denis.TradingEngine\src\Denis.TradingEngine.Exchange.Crypto\appsettings.crypto.bitfinex.json";
var text = File.ReadAllText(path);
using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
Console.WriteLine("OK");
