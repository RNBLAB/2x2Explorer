namespace Explorer.App.ViewModels;

/// <summary>주소 표시줄의 경로 조각 하나입니다. 보이는 이름과 눌렀을 때 갈 경로를 담습니다.</summary>
/// <param name="Name">단추에 보일 글자입니다.</param>
/// <param name="FullPath">누르면 옮겨 갈 경로입니다.</param>
public sealed record PathCrumb(string Name, string FullPath);
