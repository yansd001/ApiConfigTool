# New API 余额接口核对

核对日期：2026-11-15。依据官方仓库 `QuantumNous/new-api`，提交 `57345547be11b8cc0d58252622d7319545fc6fdd`。这是源码能力核对，不代表每个站点都已部署该提交。

## 查询接口

```http
GET /api/usage/token/
Authorization: Bearer sk-your-key
Accept: application/json
```

`router/api-router.go` 中该路由使用 `TokenAuthReadOnly()`；`controller/token.go` 的 `GetTokenUsage()` 返回：

```json
{
  "code": true,
  "message": "ok",
  "data": {
    "object": "token_usage",
    "total_available": 2500000,
    "total_used": 500000,
    "unlimited_quota": false,
    "expires_at": 0,
    "user_quota": {
      "total_available": 21250000,
      "total_used": 1000000
    }
  }
}
```

上述数字为示例。顶层 `data.total_available` 属于 Key；`data.user_quota.total_available` 属于账号。只有 `token.AllowUserQuotaQuery` 为真时才返回 `user_quota`。不能把没有 `user_quota`、Key 不限额或查询失败解释成账号余额为零或无限。

`web/src/features/keys/components/api-keys-mutate-drawer.tsx` 在 API Key 编辑页面提供 `allow_user_quota_query` 开关，标签为 “Allow querying user quota”。新建令牌的默认值在当前源码中为开启；已有令牌取其存储值，站点旧版本可能根本没有此能力。

`middleware/auth.go` 的 `TokenAuthReadOnly()` 允许额度耗尽或过期的 Key 查询，但拒绝禁用的 Key；仍检查 IP 白名单及账号是否被封禁。不需要用户面板 Access Token 或用户 ID。

## 额度单位

`GET /api/status` 无需 API Key。`controller/misc.go` 提供以下相关字段：

| 字段 | 用途 |
| --- | --- |
| `quota_per_unit` | 每 1 美元对应的内部额度 |
| `quota_display_type` | USD / CNY / CUSTOM / TOKENS |
| `usd_exchange_rate` | 人民币兑换比例 |
| `custom_currency_symbol` | 自定义币种符号 |
| `custom_currency_exchange_rate` | 每 1 美元对应的自定义币种金额 |
| `display_in_currency` | 旧站点兼容字段 |

站点金额 = 原始额度 ÷ `quota_per_unit` × 展示币种兑换比例。`common/constants.go` 中默认 `QuotaPerUnit` 为 500000，但本工具不把它当作站点未知时的默认金额换算参数。无法取得有效参数或展示类型为 TOKENS 时，显示原始额度。

## 可复核的官方源文件

固定提交链接：

- [路由与认证中间件绑定](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/router/api-router.go)
- [GetTokenUsage / 账号额度权限判断](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/controller/token.go)
- [TokenAuthReadOnly](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/middleware/auth.go)
- [公共状态和换算参数](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/controller/misc.go)
- [币种定义](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/setting/operation_setting/general_setting.go)
- [Key 编辑页面开关](https://github.com/QuantumNous/new-api/blob/57345547be11b8cc0d58252622d7319545fc6fdd/web/src/features/keys/components/api-keys-mutate-drawer.tsx)

本项目的 `Tests/BalanceChecks.cs` 覆盖标准响应、仅 Key 额度、账号为零、无限 Key、币种换算、无效数据、权限错误、HTTP 429、去重、刷新间隔、并发与取消。未使用用户真实 Key 做在线验证。
