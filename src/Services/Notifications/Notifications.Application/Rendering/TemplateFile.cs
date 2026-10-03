namespace Notifications.Application.Rendering;

/// <summary>One shipped file as read, before <see cref="TemplateSet.Parse"/> checks it.</summary>
public sealed record TemplateFile(string Name, string Text);
