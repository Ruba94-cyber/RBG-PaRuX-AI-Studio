namespace PaRuX.Services
{
    /// <summary>A typed Core refusal surfaced as a normal failure whose message is already plain English.</summary>
    public sealed class ModalityRefusalException : LocalAiException
    {
        public ModalityRefusal Refusal { get; }

        public ModalityRefusalException(ModalityRefusal refusal)
            : base(ModalityRefusalPresenter.Describe(refusal)) => Refusal = refusal;
    }
}
