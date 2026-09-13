namespace BitosX.Core.Domain;

/// <summary>The kind of secret stored in a vault item. Extended in future milestones.</summary>
public enum VaultItemType
{
    /// <summary>A website / application login (username + password).</summary>
    Login = 0,
}
