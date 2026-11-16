# 余额查询

默认模式沿用旧版 New API 查询规则：从 API 地址推导站点目录（`/gateway/v1` 对应 `/gateway/api/usage/token/`），携带配置中的 API Key 查询。支持账号余额的站点会返回 `data.user_quota`；没有该字段时只显示 Key 额度。账号为零、Key 不限额、账号查询未开放和网络失败分别处理。

金额来自 `/api/status` 的 `quota_per_unit`、`quota_display_type` 和汇率参数；未获取参数时显示原始额度，不假定站点汇率。接口依据 [New API 的 GetTokenUsage 实现](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/controller/token.go)，站点实际版本可能不同。

## 自定义脚本

编辑配置 → 余额查询 → 自定义脚本。支持 CC Switch 的同步 `request` / `extractor` 格式；不支持在提取器中自行调用 `fetch` 或读取系统文件。

```javascript
({
  request: {
    url: "{{baseUrl}}/user/balance",
    method: "GET",
    headers: { Authorization: "Bearer {{apiKey}}" }
  },
  extractor: function(response) {
    if (response.success === false) return { isValid: false };
    return {
      planName: "账号余额",
      remaining: Number(response.balance),
      unit: "USD"
    };
  }
})
```

`baseUrl` 和 `apiKey` 默认来自基础配置，也可单独覆盖。`accessToken` 和 `userId` 来自查询页的对应输入框；这两个值不会从系统或浏览器读取。占位符应放在 JavaScript 字符串中，支持单引号、双引号和模板字符串。变量安全转义后替换。

请求支持 GET / POST、`headers` 字符串字典和可选 `body`（JSON 对象或原始字符串）。请求 URL 可指向独立的账单服务，凭据仅按脚本请求头或请求体发送；不会自动把 API Key 添加到自定义请求。HTTP 和 HTTPS 均可用，保留自建本地服务支持；不跟随重定向。

提取器返回对象或 1–20 个对象的数组：

| 字段 | 含义 |
| --- | --- |
| `remaining` | 剩余数值，允许负余额；缺失时可用 total − used 推导 |
| `used` / `total` | 已用 / 总额，悬停余额可查看 |
| `unit` | USD、CNY、额度、次等单位 |
| `planName` | 展示名称，默认“余额” |
| `extra` | 附加说明，悬停可查看 |
| `isValid` | false 时显示查询失败，不展示伪造的零余额 |

至少返回一个有限数值，不接受空数据、NaN 或 Infinity。远端 `invalidMessage` 和错误正文不会直接展示，以免泄露密钥；脚本语法和执行错误使用固定提示。

## 刷新与保存

- 默认新配置及旧配置均启用自动 New API 查询，每 5 分钟更新当前页面展示的配置。
- 间隔设为 0 时仅手动或点击“测试余额查询”时请求；关闭后不再查询。
- 相同查询在不同应用、不同模型之间共享运行期缓存，最多同时执行 3 个请求。API Key、地址或脚本变化后重新查询。
- 手动刷新至少间隔 10 秒；限流按 Retry-After 延迟 5–60 分钟。权限或不支持的接口自动延后 30 分钟。
- 不支持 New API 的站点可改用自定义模式；错误不会阻止保存或启用 API 配置。
- 点击“保存”只保存余额设置与配置记录；点击“保存并启用”才同时启用客户端 API 配置。
- 余额只在工具运行期间查询，不包含账号登录模式的 OAuth 配额查询。
