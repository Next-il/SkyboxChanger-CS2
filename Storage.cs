using Dapper;
using MySqlConnector;

namespace SkyboxChanger;

public class SkyData
{
  public ulong SteamID { get; set; }
  public string Skybox { get; set; } = "";
  public float Brightness { get; set; } = 1.0f;
  public int Color { get; set; } = int.MaxValue;

  public bool HasSkybox()
  {
    return Skybox != "";
  }
}

public class Storage
{
  private string DbConnString { get; set; }
  private string Table { get; set; } = "";

  // Dictionary for O(1) lookup by SteamID
  private readonly Dictionary<ulong, SkyData> _playerStorage = new();
  private readonly object _storageLock = new();

  private const string UpsertQuery =
    "INSERT INTO `{0}` (`steamid`, `skybox`, `brightness`, `color`) " +
    "VALUES (@SteamID, @Skybox, @Brightness, @Color) " +
    "ON DUPLICATE KEY UPDATE `skybox` = @Skybox, `brightness` = @Brightness, `color` = @Color;";

  public Storage(string host, int port, string user, string password, string database, string tablePrefix)
  {
    DbConnString = $"Host={host};Port={port};User={user};Password={password};Database={database};AllowPublicKeyRetrieval=true;";
    Table = tablePrefix + "playerstorage";

    using MySqlConnection connection = ConnectAsync().GetAwaiter().GetResult();
    connection.Execute(
      $"CREATE TABLE IF NOT EXISTS `{Table}` (" +
      "`steamid` BIGINT UNSIGNED NOT NULL, " +
      "`skybox` VARCHAR(255) NOT NULL, " +
      "`brightness` FLOAT DEFAULT 1.0, " +
      "`color` INT NOT NULL, " +
      "PRIMARY KEY (`steamid`)) ENGINE = InnoDB;"
    );
    Load();
  }

  public async Task<MySqlConnection> ConnectAsync()
  {
    MySqlConnection connection = new(DbConnString);
    await connection.OpenAsync();
    return connection;
  }

  public void Load()
  {
    using MySqlConnection connection = ConnectAsync().GetAwaiter().GetResult();
    var result = connection.Query<SkyData>($"SELECT * FROM `{Table}`;");

    lock (_storageLock)
    {
      _playerStorage.Clear();
      foreach (var data in result)
      {
        _playerStorage[data.SteamID] = data;
      }
    }
  }

  /// <summary>Loads (or refreshes) a single player's data from the database.</summary>
  public async Task LoadPlayerAsync(ulong steamid)
  {
    using MySqlConnection connection = await ConnectAsync();
    var result = await connection.QueryFirstOrDefaultAsync<SkyData>(
      $"SELECT * FROM `{Table}` WHERE `steamid` = @SteamID;",
      new { SteamID = (long)steamid }
    );

    lock (_storageLock)
    {
      if (result != null)
      {
        _playerStorage[result.SteamID] = result;
      }
      // If no row exists yet, leave the cache empty — GetPlayerSkydata will create a default entry.
    }
  }

  /// <summary>Removes a player's entry from the in-memory cache.</summary>
  public void InvalidateCache(ulong steamid)
  {
    lock (_storageLock)
    {
      _playerStorage.Remove(steamid);
    }
  }

  public void Save(ulong? steamid = null)
  {
    if (steamid == null)
    {
      List<SkyData> snapshot;
      lock (_storageLock)
      {
        snapshot = new List<SkyData>(_playerStorage.Values);
      }
      foreach (var data in snapshot)
      {
        _ = SaveOneAsync(data);
      }
    }
    else
    {
      SkyData? data;
      lock (_storageLock)
      {
        _playerStorage.TryGetValue(steamid.Value, out data);
      }
      if (data != null)
      {
        _ = SaveOneAsync(data);
      }
    }
  }

  /// <summary>
  /// Saves a single player's data and returns an awaitable Task.
  ///
  /// <para>Routed through <see cref="SaveOneAsync"/> for its try/catch. Every user-visible write -
  /// skybox, brightness, tint - reaches this as a discarded task, so an exception here used to
  /// become an unobserved Task exception and vanish: a save that never happened looked exactly
  /// like a save that did, and "it won't keep my choice" could not be answered from the console.</para>
  /// </summary>
  public Task SaveAsync(ulong steamid)
  {
    SkyData? data;
    lock (_storageLock)
    {
      _playerStorage.TryGetValue(steamid, out data);
    }

    return data == null ? Task.CompletedTask : SaveOneAsync(data);
  }

  private async Task SaveOneAsync(SkyData data)
  {
    try
    {
      using MySqlConnection connection = await ConnectAsync();
      await connection.ExecuteAsync(string.Format(UpsertQuery, Table), data);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"[SkyboxChanger] DB save error for {data.SteamID}: {ex.Message}");
    }
  }

  public SkyData GetPlayerSkydata(ulong steamid)
  {
    lock (_storageLock)
    {
      if (_playerStorage.TryGetValue(steamid, out var data))
      {
        return data;
      }

      var newData = new SkyData { SteamID = steamid };
      _playerStorage[steamid] = newData;
      return newData;
    }
  }
}
