namespace TChatS.Protocol;

/// <summary>
/// 通用协议消息模型，不绑定具体业务协议或网络层协议。
/// </summary>
/// <param name="RawContent">原始文本内容（已去除帧分隔符）</param>
/// <param name="ConnectionId">来源连接 ID，由传输层分配</param>
public readonly record struct ProtocolMessage(
    string RawContent,
    long ConnectionId
);
