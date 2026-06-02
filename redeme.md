### 启动服务器
dotnet run --project src/TChatS.Service

### 压测
dotnet run --project src/TChatS.StressTest -c Release  --scenario chat-throughput  --connections 300  --chat-rooms 1  --messages-per-sec 5  --message-size 128  --duration 60  --ramp-up 10