#! /bin/bash

if [ -z "$1" ]; then
    rootDirectory=.
else
    rootDirectory=$1
fi

if [ -z "$2" ]; then
    needVSMac=true
    vsmacPath=vsmac.dmg
else
    needVSMac=false
    vsmacPath=$2
fi

if [ -z "$3" ]; then
    needMono=true
    monoPath=mono.pkg
else
    needMono=false
    monoPath=$3
fi


if [ "$rootDirectory" = "--help" ]; then
    echo "symbolicate-crash.sh [path folder containing crash logs (blank for current folder)]"
    exit 0
fi

if [ ! -f $rootDirectory/CrashReport.txt ]; then
    echo "Missing $rootDirectory/CrashReport.txt"
    exit 1
fi

if grep -q "No crash log contents." "$rootDirectory/CrashReport.txt"; then
    echo "No crash log contents."
    exit 2
fi

if [ ! -f $rootDirectory/CustomLogsMetadata.xml ]; then
    echo "Missing $rootDirectory/CustomLogsMetadata.xml"
    exit 3
fi

get_version () {
    grep $1 $rootDirectory/CustomLogsMetadata.xml | cut -d '>' -f 2 | cut -d '<' -f 1
}

get_mono_version () {
    # trim the extra info off the version
    get_version $1 | cut -d ' ' -f 1
}

generate_version_json () {
    cat << EOF
[
  {
    "ProductGuid": "34937104-97FC-42A0-9159-D951135F72CA",
    "Version": "$1"
  },
  {
    "ProductGuid": "964ebddd-1ffe-47e7-8128-5ce17ffffb05",
    "Version": "$2"
  }
]
EOF
}

mount_vsmac_image () {
    echo "Mounting VSMac image"
    mkdir vsmac_mount
    VSMAC_DISK=$(hdiutil attach -mountroot vsmac_mount $vsmacPath | tail -1 | cut -f 1)
    echo "VSMac drive: $VSMAC_DISK"
}

unmount_vsmac_image () {
    hdiutil detach $VSMAC_DISK
    rm -rf vsmac_mount
}

extract_mono_binary () {
    echo "Extracting Mono binary"
    mkdir mono-tmp
    pushd .

    cd mono-tmp
    xar -xf ../$monoPath

    cd mono.pkg

    cat Payload | gunzip -dc | cpio -i

    popd

    cp mono-tmp/mono.pkg/Library/Frameworks/Mono.framework/Commands/mono-sgen64 ./
}

copy_assemblies () {
    find ./ -type f \( -name "*.exe" -or -name "*.dll" -or -name "*.pdb" \) -exec cp {} ../assemblies/ \;
}

gather_assemblies () {
    echo "Gathering assemblies"
    mkdir assemblies

    pushd .

    cd mono-tmp
    copy_assemblies    

    popd

    pushd .

    cd vsmac_mount
    copy_assemblies

    popd
}

symbolicate_crash () {
    echo "Symbolicating CrashReport"
    mono DumpSymbolicate/DumpSymbolicate/bin/Debug/DumpSymbolicate.exe --crashFile=$rootDirectory/CrashReport.txt --vsmacPath=vsmac_mount/Visual\ Studio/Visual\ Studio.app --monoPath=mono-tmp --outputFile=$rootDirectory/CrashReportSymbolicated.json --generateIndexFile=$rootDirectory/generatedSymbols

    mv $rootDirectory/generatedSymbols-vsmac.json.gz $rootDirectory/generatedSymbols-vsmac-$vsmVersion.json.gz
    mv $rootDirectory/generatedSymbols-mono.json.gz $rootDirectory/generatedSymbols-mono-$monoVersion.json.gz
}

vsmVersion=$(get_version "Parameter1>")
monoVersion=$(get_mono_version "Parameter5>")

echo "Getting VSMac: $vsmVersion and Mono: $monoVersion"

# Get URLs for those version
X=$(curl -i -X POST --data "$(generate_version_json $vsmVersion $monoVersion)" "https://software.xamarin.com/service/products")

# Extract URLs from Post result
urls=($(grep -o "url=\"[^\"]*" <<< "$X" | sed -n -e 's/url=\"\([^\"]\)/\1/p'))

# Download
if [ "$needVSMac" = true ]; then
    echo "Downloading VSMac: ${urls[0]} "
    curl -L -o vsmac.dmg ${urls[0]}
fi

if [ "$needMono" = true ]; then
    echo "Downloading Mono: ${urls[1]} "
    curl -L -o mono.pkg ${urls[1]}
fi

mount_vsmac_image

extract_mono_binary

#gather_assemblies

symbolicate_crash

echo "Cleaning up"
unmount_vsmac_image
if [ $"needVSMac" = true ]; then
    rm -f vsmac.dmg
fi

if [ $"needMono" = true ]; then
    rm -f mono.pkg mono-sgen64
fi
rm -rf mono-tmp
