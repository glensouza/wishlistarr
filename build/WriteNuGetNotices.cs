using System.Text.Json;

return WriteNuGetNotices.Run(args);

internal static class WriteNuGetNotices
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: WriteNuGetNotices <input.json> <output.json>");
            return 1;
        }

        string input = args[0];
        string output = args[1];
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(input));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                Console.Error.WriteLine("NuGet license report was empty.");
                return 1;
            }

            Dictionary<string, NoticePackage> packages = [];
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    Console.Error.WriteLine("NuGet license report has an entry that is not an object.");
                    return 1;
                }

                if (element.TryGetProperty("ValidationErrors", out JsonElement errors)
                    && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0))
                {
                    Console.Error.WriteLine($"Validation errors for {Text(element, "PackageId")} {Text(element, "PackageVersion")}.");
                    return 1;
                }

                string? name = Text(element, "PackageId");
                string? version = Text(element, "PackageVersion");
                string? license = Text(element, "License");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(license))
                {
                    Console.Error.WriteLine("NuGet license report has a package with a blank name, version, or license.");
                    return 1;
                }

                name = name.Trim();
                version = version.Trim();
                license = license.Trim();
                string key = name + "\n" + version;
                if (packages.TryGetValue(key, out NoticePackage? existing) && existing.License != license)
                {
                    Console.Error.WriteLine($"Package {name} {version} has conflicting licenses.");
                    return 1;
                }

                packages[key] = new NoticePackage(name, version, license);
            }

            List<NoticePackage> list = [.. packages.Values];
            list.Sort(static (left, right) =>
            {
                int byName = string.CompareOrdinal(left.Name, right.Name);
                return byName != 0 ? byName : string.CompareOrdinal(left.Version, right.Version);
            });

            string? directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream file = File.Create(output);
            using Utf8JsonWriter writer = new(file, WriterOptions);
            writer.WriteStartObject();
            writer.WritePropertyName("packages");
            writer.WriteStartArray();
            foreach (NoticePackage package in list)
            {
                writer.WriteStartObject();
                writer.WriteString("name", package.Name);
                writer.WriteString("version", package.Version);
                writer.WriteString("license", package.License);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            file.WriteByte((byte)'\n');
        }

        return 0;
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private sealed record NoticePackage(string Name, string Version, string License);
}
