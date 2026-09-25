from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ProcessRequest(AdditionalDataHolder, Parsable):
    """
    On-demand processing: `forceOcr` runs OCR even when the PDF has text; `languages` like `deu+eng`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The forceOcr property
    force_ocr: Optional[bool] = None
    # The languages property
    languages: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ProcessRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ProcessRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ProcessRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "forceOcr": lambda n : setattr(self, 'force_ocr', n.get_bool_value()),
            "languages": lambda n : setattr(self, 'languages', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("forceOcr", self.force_ocr)
        writer.write_str_value("languages", self.languages)
        writer.write_additional_data_value(self.additional_data)
    

