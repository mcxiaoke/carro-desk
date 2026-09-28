namespace CarroDesk.Core.Commands
{
    /// <summary>
    /// 能力参数描述。<see cref="AllowedValues"/> 非空即枚举白名单——
    /// 约束输出空间是便宜模型解析可靠性的第一保障（§9.3）。
    /// </summary>
    public sealed class CommandParam
    {
        public string Name { get; set; }

        /// <summary>"string" | "int" | "bool"。内核只认这三种；JSON 值规整为对应 CLR 原始类型是传输适配层的职责。</summary>
        public string Type { get; set; }

        public bool Required { get; set; }

        public string Description { get; set; }

        /// <summary>非空即枚举白名单（精确匹配，大小写敏感）。</summary>
        public string[] AllowedValues { get; set; }
    }
}
