namespace GAIRR;

/// <summary>
/// 登录用户信息（演示登录门禁注入）：主窗口创建时携带，供后续个性化展示消费。
/// 记录类型，Name=显示名，Role=角色（如 管理员/访客）。
/// </summary>
public record UserInfo(string Name, string Role);
