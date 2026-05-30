using System.Collections.Concurrent;
using System.Net.Sockets;

namespace TChatS.Transport;

/// <summary>
/// 连接管理器，负责所有活跃连接的注册、查找和移除。
/// 类似于旧版中的 Socket 池，但提供完整的生命周期管理。
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<long, TcpConnection> _connections = new();
    private long _nextId;

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
    public IReadOnlyCollection<TcpConnection> ActiveConnections => _connections.Values.ToArray();

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
        var connection = new TcpConnection(id, socket);

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
    /// 获取当前所有活跃连接的 Socket 列表。
    /// 用于连接存活检查等场景。
    /// </summary>
    public IReadOnlyList<TcpConnection> GetActiveConnections()
    {
        return _connections.Values
            .Where(c => c.IsConnected)
            .ToList();
    }
}
