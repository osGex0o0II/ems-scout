namespace EmsScout.Application.Devices;

public static class DeviceZuoClassifier
{
    public static string Classify(string? building, double? x)
    {
        if (x is null)
        {
            return string.Empty;
        }

        return building?.Trim() switch
        {
            "5号" => ClassifyBuilding5(x.Value),
            "6号" => ClassifyBuilding6(x.Value),
            _ => string.Empty,
        };
    }

    private static string ClassifyBuilding5(double x)
    {
        if (x <= 400)
        {
            return "A座";
        }

        if (x <= 616)
        {
            return "B座";
        }

        if (x <= 874)
        {
            return "C座";
        }

        if (x <= 1120)
        {
            return "D座";
        }

        return x <= 1424 ? "E座" : "F座";
    }

    private static string ClassifyBuilding6(double x)
    {
        if (x <= 650)
        {
            return "A座";
        }

        return x <= 1220 ? "B座" : "C座";
    }
}
