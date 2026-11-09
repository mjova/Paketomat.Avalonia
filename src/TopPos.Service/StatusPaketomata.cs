using System.Text.Json.Serialization;

namespace TopPos.Service
{

    public class StatusPaketomata
    {
        [JsonPropertyName("statusiPoVelicinamaPozicija")]
        public List<StatusPoVeliciniPozicije> StatusiPoVelicinamaPozicija { get; set; } = default!;

        [JsonPropertyName("posiljkePoPozicijama")]
        public List<PosiljkaPoPoziciji> PosiljkePoPozicijama { get; set; } = default!;
    }

    public class StatusPoVeliciniPozicije
    {
        [JsonPropertyName("velicina")]
        public string Velicina { get; set; } = default!;

        [JsonPropertyName("slobodno")]
        public int Slobodno { get; set; }

        [JsonPropertyName("zauzeto")]
        public int Zauzeto { get; set; }

        [JsonPropertyName("zaPreuzimanje")]
        public int ZaPreuzimanje { get; set; }
    }

    public class PosiljkaPoPoziciji
    {
        [JsonPropertyName("barkod")]
        public string Barkod { get; set; } = default!;

        [JsonPropertyName("velicinaPosiljke")]
        public string VelicinaPosiljke { get; set; } = default!;

        [JsonPropertyName("telefonPrimaoca")]
        public string TelefonPrimaoca { get; set; } = default!;

        [JsonPropertyName("iznosOtkupa")]
        public decimal IznosOtkupa { get; set; }

        [JsonPropertyName("paketVaziDo")]
        public DateTime PaketVaziDo { get; set; }

        [JsonPropertyName("brojRegistracije")]
        public int BrojRegistracije { get; set; }

        [JsonPropertyName("kodZaOtvaranje")]
        public int KodZaOtvaranje { get; set; }

        [JsonPropertyName("brojPozicije")]
        public int BrojPozicije { get; set; }

        [JsonPropertyName("velicinaPozicije")]
        public string VelicinaPozicije { get; set; }
    }
}
