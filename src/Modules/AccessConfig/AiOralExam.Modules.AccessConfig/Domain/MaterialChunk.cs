namespace AiOralExam.Modules.AccessConfig.Domain;

public class MaterialChunk
{
    public Guid Id { get; set; }
    public Guid CourseMaterialId { get; set; }
    public CourseMaterial Material { get; set; } = null!;
    public int ChunkNo { get; set; }
    public string Citation { get; set; } = null!;
    public string Text { get; set; } = null!;
}
