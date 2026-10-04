namespace DocumentParsers;

public sealed record HeadingElement(string Text, int Level) : DocumentElement;
