using FRAGA.DTOs;
using FRAGA.Services;
using Microsoft.AspNetCore.Mvc;

namespace FRAGA.Controllers;

[ApiController]
[Route("api/fraga/frete")]
[Tags("FRAGA - Frete")]
public class FragaFreteController : ControllerBase
{
    private readonly FreteService _freteService;

    public FragaFreteController(FreteService freteService)
    {
        _freteService = freteService;
    }

    [HttpPost("cotar")]
    public async Task<IActionResult> Cotar(
        [FromBody] FreteCotacaoRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _freteService.CotarAsync(request, cancellationToken);
        return response.Sucesso ? Ok(response) : BadRequest(response);
    }

    [HttpGet("regras/{cepDestino}")]
    public async Task<IActionResult> ConsultarRegras(
        string cepDestino,
        CancellationToken cancellationToken)
    {
        var response = await _freteService.ConsultarRegrasAsync(cepDestino, cancellationToken);
        return response.CepConsultado.Length == 8 ? Ok(response) : BadRequest(response);
    }
}
