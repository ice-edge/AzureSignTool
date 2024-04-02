#!/usr/bin/env groovy

pipeline {
    agent {
        label 'codesign'
    }
    tools {
        msbuild 'vs2022'
    }
    environment {
        CERT_SERIAL=credentials('ev-cert-serial')
    }
    stages {
        stage('Build') {
            steps {
                bat "\"${tool 'vs2022'}\" -t:Package build/build.csproj -p:CertSerial=${env.CERT_SERIAL}"
            }
        }
        post {
            success {
                archiveArtifacts artifacts: 'out/AzureSignTool.zip'
            }
        }
    }
    post {
        always {
            sendNotifications currentBuild.currentResult
        }
    }
}
