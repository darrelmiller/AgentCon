param([string]$Supplier = 'http://localhost:6000', [switch]$WaitForWallet)
$ErrorActionPreference = 'Stop'

function Invoke-A2aJson {
    param([string[]]$CliArguments)
    $output = & a2a @CliArguments
    if ($LASTEXITCODE -ne 0) { throw "a2a failed: $($CliArguments -join ' ')" }
    return ($output -join "`n" | ConvertFrom-Json)
}

$before = Invoke-RestMethod "$Supplier/health"
$context = "structured-smoke-$([guid]::NewGuid().ToString('N'))"
$rfq = @{
    kind = 'rfq'; schemaVersion = '1'; demoOnly = $true
    wine = 'Barolo'; vintage = 2019; cases = 12; deliveryCity = 'San Jose'
} | ConvertTo-Json -Compress
$timer = [Diagnostics.Stopwatch]::StartNew()
$quoteResponse = Invoke-A2aJson @('send', '-a', $Supplier, '--context-id', $context,
    '--data-part', $rfq, '--media-type', 'application/vnd.agentcon.demo.wine-rfq.v1+json', '-o', 'json')
$quoteTask = $quoteResponse.task
if ($quoteTask.status.state -ne 'TASK_STATE_COMPLETED') { throw "Quote failed: $($quoteTask | ConvertTo-Json -Depth 20)" }
$quote = $quoteTask.artifacts[0].parts[0].data
if ($quote.totalMinor -ne 175300) { throw 'Quote total must be USD 1753.00' }
$po = @{
    kind = 'purchase-order'; schemaVersion = '1'; demoOnly = $true
    quoteId = $quote.quoteId; purchaseOrderId = "po-$context"
} | ConvertTo-Json -Compress
$orderResponse = Invoke-A2aJson @('send', '-a', $Supplier, '--context-id', $context,
    '--data-part', $po, '--media-type', 'application/vnd.agentcon.demo.wine-purchase-order.v1+json', '-o', 'json')
$orderTask = $orderResponse.task
$timer.Stop()
if ($orderTask.status.state -ne 'TASK_STATE_AUTH_REQUIRED') { throw 'Purchase must pause for wallet authorization' }
if ($orderTask.artifacts) { throw 'Unauthenticated purchase returned an artifact' }
$page = $orderTask.status.message.metadata.'agentcon.presentation'.presentationUrl
if (!$WaitForWallet) {
    $after = Invoke-RestMethod "$Supplier/health"
    if ($after.inferenceAttempts -ne $before.inferenceAttempts) { throw 'Inference occurred during structured smoke' }
    [pscustomobject]@{
        quoteId = $quote.quoteId; taskId = $orderTask.id; contextId = $context
        state = $orderTask.status.state; presentationUrl = $page
        instructions = $orderTask.status.message.parts[0].text
        supplierInferenceAttempts = $after.inferenceAttempts - $before.inferenceAttempts
    }
    return
}
if (!$page) { throw 'Supplier verifier is not configured; no wallet presentation page supplied' }
Start-Process $page
Read-Host 'Approve the presentation in Sphereon Wallet, then press Enter to continue the SAME purchase task' | Out-Null
$orderResponse = Invoke-A2aJson @('send', '-a', $Supplier, '--context-id', $context, '--task-id', $orderTask.id,
    '--data-part', $po, '--media-type', 'application/vnd.agentcon.demo.wine-purchase-order.v1+json', '-o', 'json')
$orderTask = $orderResponse.task
if ($orderTask.status.state -ne 'TASK_STATE_COMPLETED') { throw "Purchase is still pending or failed: $($orderTask.status.state)" }
$receipt = $orderTask.artifacts[0].parts[0].data
if ($receipt.status -ne 'simulated' -or $receipt.ageVerification -ne 'verified-demo-over-21') { throw 'Invalid demo receipt' }
$fetchedResponse = Invoke-A2aJson @('task', 'get', '-a', $Supplier, $orderTask.id, '-o', 'json')
$fetched = $fetchedResponse.task
if ($fetched.artifacts[0].parts[0].data.orderId -ne $receipt.orderId) { throw 'GetTask receipt differs' }
$after = Invoke-RestMethod "$Supplier/health"
if ($after.inferenceAttempts -ne $before.inferenceAttempts) { throw 'Inference occurred during structured smoke (or concurrent NL traffic)' }
[pscustomobject]@{
    quoteId = $quote.quoteId; orderId = $receipt.orderId; taskId = $orderTask.id
    totalMinor = $quote.totalMinor; elapsedMilliseconds = $timer.ElapsedMilliseconds
    supplierInferenceAttempts = $after.inferenceAttempts - $before.inferenceAttempts
    ageVerification = $receipt.ageVerification
}
