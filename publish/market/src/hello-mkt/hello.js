// 示例市场插件脚本：把输入原样回显（验证市场安装 → 热加载 → 调用全链路）。
// 入参经命令行传入，字面 \n 还原为真实换行（与 dingtalk.js 同约定）。
const input = (process.argv[2] || "").replace(/\\n/g, "\n");
console.log("[hello-mkt] " + input);
