# FRAGA API

API independente para cotacao de frete do ecommerce B2B.

## Como abrir no Visual Studio

Abra uma das solucoes:

```text
FRAGA.slnx
FRAGA_VisualStudio.sln
```

## Endpoints

Health:

```http
GET /api/fraga/health
```

Cotacao de frete:

```http
POST /api/fraga/frete/cotar
```

## Exemplo de Request

```json
{
  "cepDestino": "70040903",
  "cepOrigem": "71205060",
  "pesoKg": 120.5,
  "valorNota": 1500,
  "subtotalLiquido": 1500,
  "quantidadeVolumes": 3
}
```

## Configuracao do HANA

A API le a connection string de duas formas:

1. `ConnectionStrings:HanaConnection` no `appsettings.json`
2. variavel de ambiente `FRAGA_HANA_CONNECTION`

Exemplo:

```text
Server=SERVIDOR:30015;UserID=USUARIO;Password=SENHA;Current Schema=SBO_ELETROPAR_PRD
```

## Teste no Visual Studio

Use o arquivo:

```text
FRAGA.http
```

Ele ja possui uma chamada de health e uma chamada de cotacao pronta.
