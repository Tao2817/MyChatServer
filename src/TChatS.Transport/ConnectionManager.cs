using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TChatS.Transport;

/// <summary>
/// 连接管理器，负责所有活跃连接的注册、查找和移除。
/// 类似于旧版中的 Socket 池，但提供完整的生命周期管理。
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<long, TcpConnection> _connections = new();
    private readonly ILoggerFactory? _loggerFactory;
    private long _nextId;

    public ConnectionManager(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// 最大连接数，0 表示无限制。
    /// </summary>
    public int MaxConnections { get; init; } = 1000;

    /// <summary>
    /// 当前活跃连接数。
    /// </summary>
    public int ActiveCount => _connections.Count;

    /// <summary>
    /// 获取所有活跃连接的快照（用于广播等场景）。
    /// </summary>
    // 枚举键值对而非 .Values，避免 ConcurrentDictionary 锁+快照开销
    public IReadOnlyCollection<TcpConnection> ActiveConnections => _connections.Select(kvp => kvp.Value).ToArray();

    /// <summary>
    /// 接受一个新的 Socket 连接并注册到管理器中。
    /// </summary>
    /// <param name="socket">已 Accept 的客户端 Socket</param>
    /// <returns>注册后的 <see cref="TcpConnection"/> 对象</returns>
    /// <exception cref="InvalidOperationException">连接数已达上限</exception>
    public TcpConnection Accept(Socket socket)
    {
        if (MaxConnections > 0 && _connections.Count >= MaxConnections)
        {
            socket.Close();
            throw new InvalidOperationException(
                $"连接数已达上限 {MaxConnections}，拒绝新连接。");
        }

        var id = Interlocked.Increment(ref _nextId);
        var logger = _loggerFactory?.CreateLogger<TcpConnection>();
        var connection = new TcpConnection(id, socket, logger);

        if (!_connections.TryAdd(id, connection))
        {
            connection.Dispose();
            throw new InvalidOperationException($"连接 ID {id} 冲突（不应发生）。");
        }

        return connection;
    }

    /// <summary>
    /// 移除并销毁指定连接。
    /// </summary>
    /// <param name="id">连接 ID</param>
    /// <returns>是否成功移除</returns>
    public bool RemoveConnection(long id)
    {
        if (_connections.TryRemove(id, out var connection))
        {
            connection.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 根据 ID 查找连接。
    /// </summary>
    /// <param name="id">连接 ID</param>
    /// <returns>找到的 <see cref="TcpConnection"/>，或 null</returns>
    public TcpConnection? GetConnection(long id)
    {
        _connections.TryGetValue(id, out var connection);
        return connection;
    }

    /// <summary>
    /// 获取当前所有活跃连接（IsConnected == true）。
    /// 用于连接存活检查等场景。
    /// </summary>
    public IReadOnlyList<TcpConnection> GetActiveConnections()
    {
        // 枚举键值对而非 .Values，避免 ConcurrentDictionary 锁+快照开销
        return _connections
            .Where(kvp => kvp.Value.IsConnected)
            .Select(kvp => kvp.Value)
            .ToList();
    }

    /// <summary>
    /// 获取内部所有连接（包括已断开但尚未移除的），用于调试/监控。
    /// </summary>
    public IReadOnlyList<TcpConnection> GetAllConnections()
    {
        return _connections
            .Select(kvp => kvp.Value)
            .ToList();
    }

}
